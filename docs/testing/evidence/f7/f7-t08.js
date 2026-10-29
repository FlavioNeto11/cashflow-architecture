// F7 / T-08 (RNF-02, RNF-03, RNF-09, RNF-10; ADR-0009): consulta do consolidado diario a taxa constante pelo gateway real,
// com JWT real do Keycloak (client credentials; o token fica so na memoria do VU e nunca e impresso nem gravado).
//
// Metodo (ADR-0009, opcao A): constant-arrival-rate, 1 requisicao por iteracao, timeout de cliente de 2 s.
//   perda = (5xx + 429 + timeouts + erros de conexao/TLS) / requisicoes da janela de medicao
//   4xx diferentes de 429 em carga so valida sao ANOMALIAS (contadas a parte). 2xx com corpo divergente do oraculo reprova.
// dropped_iterations > 0 na janela de medicao => ensaio INVALIDO para RNF-02 (o gerador nao entregou a taxa pedida).
//
// Parametros (variaveis de ambiente do container): GW_URL, KC_URL, DATE, EXPECT (JSON por merchant), TOKENS ("A" ou "AB"),
//   GET_RATE (req/s totais), WARMUP_S, DURATION_S, INCLUDE_CLOSING (0|1), POST_RATE (req/s, 0 = sem POST), POST_DATE,
//   POST_AMOUNT, LABEL, MAX_VUS. Segredos dos clientes: KEYCLOAK_MERCHANT_{A,B}_SYSTEM_SECRET (herdados do ambiente do docker run).
import http from 'k6/http';
import exec from 'k6/execution';
import { Counter, Trend } from 'k6/metrics';

const env = (k, d) => (__ENV[k] !== undefined && __ENV[k] !== '' ? __ENV[k] : d);
const GW = env('GW_URL', 'https://gateway:8080');
const KC = env('KC_URL', 'http://keycloak:8080/realms/cashflow/protocol/openid-connect/token');
const DATE = env('DATE', '2026-02-10');
const EXPECT = JSON.parse(env('EXPECT', '{}'));
const TOKENS = env('TOKENS', 'A');
const GET_RATE = parseInt(env('GET_RATE', '50'));
const POST_RATE = parseInt(env('POST_RATE', '0'));
const WARMUP = parseInt(env('WARMUP_S', '20'));
const DURATION = parseInt(env('DURATION_S', '120'));
const CLOSING = env('INCLUDE_CLOSING', '0') === '1';
const POST_DATE = env('POST_DATE', '2026-02-11');
const POST_AMOUNT = env('POST_AMOUNT', '1.00');
const LABEL = env('LABEL', 'run');
const RUN = env('RUN_ID', Date.now().toString(36));
const MAX_VUS = parseInt(env('MAX_VUS', '200'));
const MERCHANT = { A: '33333333-3333-4333-8333-333333333333', B: '44444444-4444-4444-8444-444444444444' };
const SECRET = { A: __ENV.KEYCLOAK_MERCHANT_A_SYSTEM_SECRET, B: __ENV.KEYCLOAK_MERCHANT_B_SYSTEM_SECRET };
const CLIENT = { A: 'merchant-a-system', B: 'merchant-b-system' };
const SCOPE = 'transactions.write transactions.read consolidation.read';

// metricas da JANELA DE MEDICAO (a aquecimento nao entra)
const getLat = new Trend('f7_get_latency_ms', true);
const getOk = new Counter('f7_get_2xx_correct');
const getWrong = new Counter('f7_get_2xx_wrong_body');
const get429 = new Counter('f7_get_429');
const get5xx = new Counter('f7_get_5xx');
const getTimeout = new Counter('f7_get_timeout_or_conn_error');
const get4xxOther = new Counter('f7_get_other_4xx_anomaly');
const postLat = new Trend('f7_post_latency_ms', true);
const post201 = new Counter('f7_post_201');
const post201All = new Counter('f7_post_201_all_phases'); // inclui aquecimento: confronto com a fonte (PostgreSQL)
const post429 = new Counter('f7_post_429');
const post5xx = new Counter('f7_post_5xx');
const postTimeout = new Counter('f7_post_timeout_or_conn_error');
const postOther = new Counter('f7_post_other');
const tokenFail = new Counter('f7_token_fetch_failed');
const tokenFetch = new Counter('f7_token_fetches'); // todas as buscas de token (todas as fases): explica http_reqs > requisicoes medidas

export const options = (() => {
  const sc = {};
  const maxVUs = MAX_VUS;
  const get = (name, start, dur, rate) => ({
    executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration: `${dur}s`, startTime: `${start}s`,
    preAllocatedVUs: 100, maxVUs, exec: 'doGet', gracefulStop: '5s',
  });
  const post = (name, start, dur, rate) => ({
    executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration: `${dur}s`, startTime: `${start}s`,
    preAllocatedVUs: 20, maxVUs: 60, exec: 'doPost', gracefulStop: '5s',
  });
  const gap = 2;
  if (GET_RATE > 0) {
    if (WARMUP > 0) sc.get_warmup = get('get_warmup', 0, WARMUP, GET_RATE);
    sc.get_measure = get('get_measure', WARMUP + gap, DURATION, GET_RATE);
  }
  if (POST_RATE > 0) {
    if (WARMUP > 0) sc.post_warmup = post('post_warmup', 0, WARMUP, POST_RATE);
    sc.post_measure = post('post_measure', WARMUP + gap, DURATION, POST_RATE);
  }
  return {
    scenarios: sc,
    summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
    thresholds: {
      // thresholds "vazios" so para materializar sub-metricas por cenario no resumo
      'dropped_iterations{scenario:get_measure}': ['count>=0'],
      'dropped_iterations{scenario:post_measure}': ['count>=0'],
      'http_reqs{scenario:get_measure}': ['count>=0'],
      'http_reqs{scenario:post_measure}': ['count>=0'],
      'iterations{scenario:get_measure}': ['count>=0'],
      'iterations{scenario:post_measure}': ['count>=0'],
    },
    insecureSkipTLSVerify: false,
    noConnectionReuse: false,
  };
})();

// token por VU e por merchant, renovado 30 s antes de expirar (requisicao de token marcada name:token, fora das metricas f7_*)
const cache = {};
function bearer(who) {
  const now = Date.now();
  const c = cache[who];
  if (c && now < c.exp - 30000) return c.tok;
  tokenFetch.add(1);
  const r = http.post(KC, { grant_type: 'client_credentials', client_id: CLIENT[who], client_secret: SECRET[who], scope: SCOPE },
    { tags: { name: 'token' }, timeout: '10s' });
  if (r.status !== 200) { tokenFail.add(1); return null; }
  const j = r.json();
  cache[who] = { tok: j.access_token, exp: now + j.expires_in * 1000 };
  return cache[who].tok;
}

const measuring = () => exec.scenario.name.endsWith('_measure');
// TOKENS=B (Passo B, 21-04): so o merchant B, usado como VITIMA em paralelo a rajada do merchant A
const pick = () => (TOKENS === 'AB' ? (exec.scenario.iterationInTest % 2 === 0 ? 'A' : 'B') : (TOKENS === 'B' ? 'B' : 'A'));

export function doGet() {
  const who = pick();
  const tok = bearer(who);
  if (!tok) return;
  const url = `${GW}/api/v1/merchants/${MERCHANT[who]}/consolidations/daily?date=${DATE}${CLOSING ? '&include=closingBalance' : ''}`;
  const r = http.get(url, { headers: { Authorization: `Bearer ${tok}` }, timeout: '2s', tags: { name: 'daily' },
    responseCallback: http.expectedStatuses(200) });
  if (!measuring()) return;
  getLat.add(r.timings.duration);
  if (r.status === 200) {
    const e = EXPECT[who];
    let ok = false;
    try {
      const b = r.json();
      ok = b.merchantId === MERCHANT[who] && b.date === DATE && b.credits === e.credits && b.debits === e.debits &&
        b.dailyNet === e.net && b.entriesCount === e.count && (!CLOSING || b.closingBalance === e.closing);
    } catch (_) { ok = false; }
    (ok ? getOk : getWrong).add(1);
  } else if (r.status === 429) get429.add(1);
  else if (r.status >= 500) get5xx.add(1);
  else if (r.status === 0) getTimeout.add(1);
  else get4xxOther.add(1);
}

export function doPost() {
  const tok = bearer('A');
  if (!tok) return;
  const key = `f7-k6-${RUN}-${exec.scenario.name}-${exec.scenario.iterationInTest}`;
  const body = JSON.stringify({ type: 'CREDIT', amount: POST_AMOUNT, effectiveDate: POST_DATE, description: `f7-k6-${RUN}` });
  const r = http.post(`${GW}/api/v1/merchants/${MERCHANT.A}/transactions`, body,
    { headers: { Authorization: `Bearer ${tok}`, 'Idempotency-Key': key, 'Content-Type': 'application/json' }, timeout: '2s',
      tags: { name: 'post' }, responseCallback: http.expectedStatuses(201) });
  if (r.status === 201) post201All.add(1);
  if (!measuring()) return;
  postLat.add(r.timings.duration);
  if (r.status === 201) post201.add(1);
  else if (r.status === 429) post429.add(1);
  else if (r.status >= 500) post5xx.add(1);
  else if (r.status === 0) postTimeout.add(1);
  else postOther.add(1);
}

const v = (data, name, key) => (data.metrics[name] && data.metrics[name].values ? data.metrics[name].values[key] : undefined);
export function handleSummary(data) {
  const trend = (n) => {
    const t = data.metrics[n];
    if (!t || !t.values) return null;
    const x = t.values;
    return { count: undefined, avg: x.avg, min: x.min, med: x.med, p90: x['p(90)'], p95: x['p(95)'], p99: x['p(99)'], max: x.max };
  };
  const c = (n) => v(data, n, 'count') || 0;
  const gets = c('f7_get_2xx_correct') + c('f7_get_2xx_wrong_body') + c('f7_get_429') + c('f7_get_5xx') + c('f7_get_timeout_or_conn_error') + c('f7_get_other_4xx_anomaly');
  const lossGet = c('f7_get_429') + c('f7_get_5xx') + c('f7_get_timeout_or_conn_error');
  const posts = c('f7_post_201') + c('f7_post_429') + c('f7_post_5xx') + c('f7_post_timeout_or_conn_error') + c('f7_post_other');
  const out = {
    label: LABEL, run: RUN, k6: 'v2.3.0 (imagem fixada por digest no roteiro)',
    params: { GW, DATE, TOKENS, GET_RATE, POST_RATE, WARMUP_S: WARMUP, DURATION_S: DURATION, INCLUDE_CLOSING: CLOSING, MAX_VUS },
    get: {
      measure_requests: gets,
      ok_2xx_correct: c('f7_get_2xx_correct'), wrong_body_2xx: c('f7_get_2xx_wrong_body'), r429: c('f7_get_429'), r5xx: c('f7_get_5xx'),
      timeout_or_conn_error: c('f7_get_timeout_or_conn_error'), other_4xx_anomaly: c('f7_get_other_4xx_anomaly'),
      loss_rate_adr0009: gets ? lossGet / gets : null,
      latency_ms: trend('f7_get_latency_ms'),
      dropped_iterations_measure: c('dropped_iterations{scenario:get_measure}'),
      http_reqs_measure_window_incl_token: c('http_reqs{scenario:get_measure}'),
      achieved_rps: gets ? gets / DURATION : null,
    },
    post: POST_RATE > 0 ? {
      measure_requests: posts, r201: c('f7_post_201'), r201_all_phases: c('f7_post_201_all_phases'), r429: c('f7_post_429'), r5xx: c('f7_post_5xx'),
      timeout_or_conn_error: c('f7_post_timeout_or_conn_error'), other: c('f7_post_other'),
      latency_ms: trend('f7_post_latency_ms'),
      dropped_iterations_measure: c('dropped_iterations{scenario:post_measure}'),
      achieved_rps: posts / DURATION,
    } : null,
    token_fetch_failed: c('f7_token_fetch_failed'),
    token_fetches_all_phases: c('f7_token_fetches'),
    vus_max_configured: v(data, 'vus_max', 'max'), vus_peak_used: v(data, 'vus', 'max'),
    data_received_bytes: v(data, 'data_received', 'count'),
  };
  const f = (x) => (x === null || x === undefined ? 'n/a' : (typeof x === 'number' ? x.toFixed(2) : x));
  const lt = (t) => (t ? `avg=${f(t.avg)} min=${f(t.min)} p50=${f(t.med)} p90=${f(t.p90)} p95=${f(t.p95)} p99=${f(t.p99)} max=${f(t.max)}` : 'n/a');
  let txt = `\n===== F7 ${LABEL} (run ${RUN}) =====\nparams: ${JSON.stringify(out.params)}\n`;
  if (GET_RATE > 0) {
    txt += `GET janela de medicao: ${gets} req (taxa alcancada ${f(out.get.achieved_rps)}/s) | 2xx corretos=${out.get.ok_2xx_correct} 2xx-corpo-errado=${out.get.wrong_body_2xx} 429=${out.get.r429} 5xx=${out.get.r5xx} timeout/conexao=${out.get.timeout_or_conn_error} 4xx-anomalia=${out.get.other_4xx_anomaly}\n`;
    txt += `GET perda (ADR-0009: 5xx+429+timeout+conexao)/req = ${f(out.get.loss_rate_adr0009 === null ? null : out.get.loss_rate_adr0009 * 100)}% | dropped_iterations(medicao)=${out.get.dropped_iterations_measure}\n`;
    txt += `GET latencia ms: ${lt(out.get.latency_ms)}\n`;
  }
  if (POST_RATE > 0) {
    txt += `POST janela de medicao: ${posts} req (taxa ${f(out.post.achieved_rps)}/s) | 201=${out.post.r201} 429=${out.post.r429} 5xx=${out.post.r5xx} timeout/conexao=${out.post.timeout_or_conn_error} outros=${out.post.other} dropped=${out.post.dropped_iterations_measure}\n`;
    txt += `POST latencia ms: ${lt(out.post.latency_ms)}\n`;
  }
  txt += `token_fetch_failed=${out.token_fetch_failed} token_fetches_todas_as_fases=${out.token_fetches_all_phases} vus_max_configurado=${out.vus_max_configured} metrica_vus_max_amostrada=${out.vus_peak_used}\n`;
  return { stdout: txt, ['/out/' + LABEL + '.json']: JSON.stringify(out, null, 2) };
}
