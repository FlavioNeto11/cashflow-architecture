// F7 Passo B item 4 (P1-1): geradores AUXILIARES de carga contra o consolidado diario, rodando em paralelo ao f7-t08.js.
//   MODE=flood : inundacao ANONIMA (sem Authorization) a RATE req/s, taxa de chegada constante, de um container proprio (um IP so).
//                O oraculo e do lado AUTENTICADO (f7-t08.js em paralelo); aqui so se registra o que o anonimo recebeu (401/429).
//   MODE=burst : BURSTS rajadas de BURST_N requisicoes autenticadas do merchant MERCHANT, disparadas juntas (shared-iterations com
//                BURST_N VUs), com intervalo de GAP_S s entre rajadas. O token e obtido UMA vez no setup() (nunca impresso).
//                burst_send_ms mede o espalhamento (max - min) do instante de envio dentro de cada rajada: prova que >60 requisicoes
//                sairam em menos de 1 s. 429 do PROPRIO merchant da rajada e esperado (limite por merchant 60/s); o oraculo de
//                isolamento e o merchant B em paralelo (f7-t08.js TOKENS=B).
// Parametros: MODE, GW_URL, KC_URL, DATE, MERCHANT (A|B), RATE, DURATION_S, BURST_N, BURSTS, FIRST_BURST_S, GAP_S, LABEL.
import http from 'k6/http';
import { Counter, Trend } from 'k6/metrics';

const env = (k, d) => (__ENV[k] !== undefined && __ENV[k] !== '' ? __ENV[k] : d);
const MODE = env('MODE', 'flood');
const GW = env('GW_URL', 'https://gateway:8080');
const KC = env('KC_URL', 'http://keycloak:8080/realms/cashflow/protocol/openid-connect/token');
const DATE = env('DATE', '2026-02-10');
const WHO = env('MERCHANT', 'A');
const RATE = parseInt(env('RATE', '200'));
const DURATION = parseInt(env('DURATION_S', '60'));
const BURST_N = parseInt(env('BURST_N', '90'));
const BURSTS = parseInt(env('BURSTS', '3'));
const FIRST = parseInt(env('FIRST_BURST_S', '15'));
const GAP = parseInt(env('GAP_S', '10'));
const LABEL = env('LABEL', 'aux');
const MERCHANT = { A: '33333333-3333-4333-8333-333333333333', B: '44444444-4444-4444-8444-444444444444' };
const CLIENT = { A: 'merchant-a-system', B: 'merchant-b-system' };
const SECRET = { A: __ENV.KEYCLOAK_MERCHANT_A_SYSTEM_SECRET, B: __ENV.KEYCLOAK_MERCHANT_B_SYSTEM_SECRET };
const URL = `${GW}/api/v1/merchants/${MERCHANT[WHO]}/consolidations/daily?date=${DATE}`;

const s2xx = new Counter('aux_2xx');
const s401 = new Counter('aux_401');
const s429 = new Counter('aux_429');
const s5xx = new Counter('aux_5xx');
const s0 = new Counter('aux_timeout_or_conn_error');
const sOther = new Counter('aux_other');
const lat = new Trend('aux_latency_ms', true);
const sendMs = new Trend('burst_send_ms');
const total = new Counter('aux_total');

export const options = (() => {
  const sc = {};
  if (MODE === 'flood') {
    sc.flood = { executor: 'constant-arrival-rate', rate: RATE, timeUnit: '1s', duration: `${DURATION}s`, preAllocatedVUs: 200, maxVUs: 600, exec: 'flood', gracefulStop: '5s' };
  } else {
    for (let i = 0; i < BURSTS; i++) {
      sc[`burst${i}`] = { executor: 'shared-iterations', vus: BURST_N, iterations: BURST_N, maxDuration: '10s', startTime: `${FIRST + i * GAP}s`, exec: 'burst', gracefulStop: '5s', tags: { burst: String(i) } };
    }
  }
  const th = { 'aux_total': ['count>=0'] };
  if (MODE === 'burst') for (let i = 0; i < BURSTS; i++) th[`burst_send_ms{burst:${i}}`] = ['max>=0'];
  return { scenarios: sc, thresholds: th, summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'], insecureSkipTLSVerify: false };
})();

export function setup() {
  if (MODE !== 'burst') return {};
  const r = http.post(KC, { grant_type: 'client_credentials', client_id: CLIENT[WHO], client_secret: SECRET[WHO], scope: 'consolidation.read' }, { tags: { name: 'token' } });
  if (r.status !== 200) throw new Error('token indisponivel: status ' + r.status);
  return { tok: r.json().access_token, t0: Date.now() };
}

function record(r) {
  total.add(1);
  lat.add(r.timings.duration);
  if (r.status >= 200 && r.status < 300) s2xx.add(1);
  else if (r.status === 401) s401.add(1);
  else if (r.status === 429) s429.add(1);
  else if (r.status >= 500) s5xx.add(1);
  else if (r.status === 0) s0.add(1);
  else sOther.add(1);
}

export function flood() {
  record(http.get(URL, { timeout: '2s', tags: { name: 'flood' }, responseCallback: http.expectedStatuses({ min: 200, max: 499 }) }));
}

export function burst(data) {
  // instante de envio relativo ao inicio da rajada (a rajada k comeca em FIRST + k*GAP segundos depois de t0)
  const rel = Date.now() - data.t0;
  const k = Math.max(0, Math.round((rel / 1000 - FIRST) / GAP));
  sendMs.add(rel - (FIRST + k * GAP) * 1000, { burst: String(k) });
  record(http.get(URL, { headers: { Authorization: `Bearer ${data.tok}` }, timeout: '2s', tags: { name: 'burst' }, responseCallback: http.expectedStatuses({ min: 200, max: 499 }) }));
}

export function handleSummary(data) {
  const c = (n) => (data.metrics[n] && data.metrics[n].values ? data.metrics[n].values.count || 0 : 0);
  const t = (n) => (data.metrics[n] && data.metrics[n].values ? data.metrics[n].values : null);
  const L = t('aux_latency_ms');
  const out = {
    label: LABEL, mode: MODE, params: { GW, DATE, MERCHANT: WHO, RATE, DURATION_S: DURATION, BURST_N, BURSTS, FIRST_BURST_S: FIRST, GAP_S: GAP },
    total: c('aux_total'), s2xx: c('aux_2xx'), s401: c('aux_401'), s429: c('aux_429'), s5xx: c('aux_5xx'), timeout_or_conn_error: c('aux_timeout_or_conn_error'), other: c('aux_other'),
    latency_ms: L ? { avg: L.avg, med: L.med, p90: L['p(90)'], p95: L['p(95)'], p99: L['p(99)'], max: L.max } : null,
    dropped_iterations: c('dropped_iterations'),
    burst_send_spread_ms: {},
  };
  if (MODE === 'burst') for (let i = 0; i < BURSTS; i++) { const m = t(`burst_send_ms{burst:${i}}`); if (m) out.burst_send_spread_ms[`burst${i}`] = { min: m.min, max: m.max, spread: m.max - m.min }; }
  const f = (x) => (x === null || x === undefined ? 'n/a' : x.toFixed(1));
  let txt = `\n===== F7 aux ${LABEL} (${MODE}) =====\nparams: ${JSON.stringify(out.params)}\n`;
  txt += `total=${out.total} 2xx=${out.s2xx} 401=${out.s401} 429=${out.s429} 5xx=${out.s5xx} timeout/conexao=${out.timeout_or_conn_error} outros=${out.other} dropped_iterations=${out.dropped_iterations}\n`;
  if (out.latency_ms) txt += `latencia ms: avg=${f(out.latency_ms.avg)} p50=${f(out.latency_ms.med)} p95=${f(out.latency_ms.p95)} p99=${f(out.latency_ms.p99)} max=${f(out.latency_ms.max)}\n`;
  if (MODE === 'burst') txt += `espalhamento do envio por rajada (ms, max-min): ${JSON.stringify(out.burst_send_spread_ms)}\n`;
  return { stdout: txt, ['/out/' + LABEL + '.json']: JSON.stringify(out, null, 2) };
}
