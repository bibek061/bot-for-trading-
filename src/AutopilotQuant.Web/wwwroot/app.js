'use strict';
const $ = id => document.getElementById(id);
const referenceChart = new TradingViewReference.ReferenceChart(document);
let accessKey = '', state, selectedSymbol = 'MES', currentReplay, polling = false;
let providerState, marketDataDirty = false, marketDataBusy = false;
let publicDataState, publicRefreshBusy = false, publicAutoNext = 0;
const publicHistory = new Map();
const isEtf = () => ['SPY','QQQ'].includes(selectedSymbol);
function publicAuto(enabled) { $('public-auto').checked = enabled; $('overview-public-auto').checked = enabled; }
const usd = value => new Intl.NumberFormat('en-US', {style:'currency',currency:'USD'}).format(value);
const number = value => new Intl.NumberFormat('en-US', {maximumFractionDigits:2}).format(value);
const percent = value => `${(value * 100).toFixed(2)}%`;
const time = value => value ? new Date(value).toLocaleTimeString('en-US', {hour:'2-digit',minute:'2-digit',second:'2-digit'}) : '—';
const date = value => new Date(value).toLocaleString();
const text = (id, value) => { $(id).textContent = value; };
function message(value) { text('message', value); $('message').hidden = false; }
async function api(path, options = {}) {
  const response = await fetch(`/api/${path}`, { ...options, headers: {'X-Dashboard-Key':accessKey,...options.headers} });
  const body = await response.json().catch(() => ({error:`Server returned ${response.status}`}));
  if (!response.ok) { if (response.status === 401) lock(); throw new Error(body.error || `Request failed (${response.status})`); }
  return body;
}
function lock() { accessKey = ''; referenceChart.close(); publicAuto(false); publicHistory.clear(); $('workspace').hidden = true; $('login').hidden = false; $('access-key').value = ''; }
$('lock').onclick = lock;
$('unlock-form').onsubmit = async event => {
  event.preventDefault(); accessKey = $('access-key').value.trim();
  try {
    const result = await api('state');
    $('access-key').value = ''; $('login').hidden = true; $('workspace').hidden = false;
    selectedSymbol = result.provider.configured ? (result.provider.settings.instruments.find(i => i.enabled)?.symbol || 'MES')
      : result.publicData?.secretPresent ? 'SPY' : 'MES';
    document.querySelectorAll('[data-symbol]').forEach(b => b.classList.toggle('selected', b.dataset.symbol === selectedSymbol));
    render(result); populateSettings(); populateMarketData(); await loadReplays();
    if (isEtf() && !$('overview').hidden && !document.hidden && result.publicData?.secretPresent) { publicAuto(true); await refreshPublicQuotes(); }
  } catch (error) { text('login-message', error.message); }
};
function table(id, rows, empty, columns) {
  const body = $(id); body.replaceChildren();
  if (!rows.length) { const row = body.insertRow(); const cell = row.insertCell(); cell.colSpan = columns; cell.className = 'empty-cell'; cell.textContent = empty; return; }
  rows.forEach(values => { const row = body.insertRow(); values.forEach(value => { const cell = row.insertCell(); if (value instanceof Node) cell.append(value); else cell.textContent = value; }); });
}
function pnl(id, value) { text(id, usd(value)); $(id).classList.toggle('positive', value > 0); $(id).classList.toggle('negative', value < 0); }
function render(result) {
  state = result.session;
  text('connection', '● Connected locally'); $('connection').classList.remove('negative');
  text('server-time', date(state.serverTime)); text('equity', usd(state.equity));
  pnl('realized', state.realizedNet); pnl('unrealized', state.unrealizedGross);
  text('drawdown', percent(state.drawdownPct)); text('drawdown-limit', `Pause limit ${percent(state.settings.maxDrawdownPct)}`);
  text('bot-state', state.riskHalted ? 'RISK HALT' : state.paused ? 'PAUSED' : 'ARMED');
  text('pause-reason', state.pauseReason); text('position-count', state.positions.length);
  text('daily-loss', `${percent(state.dailyLossPct)} / ${percent(state.settings.dailyLossLimitPct)}`);
  text('risk-halt', state.riskHalted ? 'Active' : 'Clear'); text('fees', usd(state.fees));
  renderMarketData(result.provider);
  renderPublicData(result.publicData);
  text('mark-status', state.positions.length === 0 ? 'No open paper exposure' : state.positions.some(p => !state.instruments.find(i => i.symbol === p.symbol)?.fresh) ? 'STALE MARKS · exposure needs fresh data' : 'Marked at latest received bid prices');
  $('resume').disabled = !state.paused || state.riskHalted || !state.instruments.some(i => i.fresh);
  table('positions', state.positions.map(p => { const protection = state.protection.find(x => x.positionId === p.positionId); return [p.symbol,`${p.side} / ${p.quantity}`,number(p.entryPrice),protection ? number(protection.stopPrice) : 'MISSING',protection ? number(protection.targetPrice) : 'MISSING',date(p.openedAtUtc)]; }), 'No open paper positions. New entries require an armed strategy and fresh data.', 6);
  table('fills', state.fills.map(f => [time(f.filledAtUtc),f.symbol,`${f.side} ${f.quantity}`,number(f.fillPrice),usd(f.fee)]), 'No paper fills yet.', 5);
  $('events').replaceChildren(); state.events.forEach(event => {
    const row = document.createElement('div'); row.className = 'event';
    const stamp = document.createElement('time'); stamp.textContent = time(event.at); stamp.title = date(event.at);
    const copy = document.createElement('div'), title = document.createElement('strong'), detail = document.createElement('p');
    title.textContent = event.kind; detail.textContent = event.message; copy.append(title,detail); row.append(stamp,copy); $('events').append(row);
  }); renderChart();
}
async function refresh() {
  if (!accessKey || polling) return; polling = true;
  try { render(await api('state')); }
  catch (error) {
    text('connection', '● Server unavailable'); $('connection').classList.add('negative');
    $('resume').disabled = true; text('quote-state', 'UNVERIFIED');
    publicAuto(false); text('public-data-status', 'UNVERIFIED');
    table('public-quotes', [], 'Server unavailable. Refresh after reconnecting to verify quote timestamps.', 7);
    text('mark-status', 'SERVER UNREACHABLE · displayed values may be stale');
    message(error.message);
  } finally { polling = false; }
}
setInterval(refresh, 1000);
document.querySelectorAll('[data-panel]').forEach(button => button.onclick = () => {
  const name = button.dataset.panel;
  document.querySelectorAll('.panel-page').forEach(panel => panel.hidden = panel.id !== name);
  document.querySelectorAll('[data-panel]').forEach(nav => { nav.classList.toggle('active',nav === button); if (nav === button) nav.setAttribute('aria-current','page'); else nav.removeAttribute('aria-current'); });
  text('breadcrumb', {overview:'Overview',research:'Replay lab',risk:'Risk & settings','market-data':'Market data',tradingview:'TradingView'}[name]);
  text('page-title', {overview:'Paper overview',research:'Replay lab',risk:'Risk & settings','market-data':'Market data',tradingview:'TradingView research'}[name]);
  text('page-description', {overview:'Your strategy, exposure, and execution in one place.',research:'Turn your historical data into inspectable research.',risk:'Set boundaries before your strategy takes a position.','market-data':'Prepare your connection. Know where every price comes from.',tradingview:'Explore futures charts and broader market context.'}[name]);
  if (name !== 'tradingview') referenceChart.close();
  if (name !== 'market-data' && !(name === 'overview' && isEtf())) publicAuto(false);
  if (name === 'overview') renderChart();
  renderOverviewNotice();
});
document.querySelectorAll('[data-symbol]').forEach(button => button.onclick = () => {
  selectedSymbol = button.dataset.symbol;
  document.querySelectorAll('[data-symbol]').forEach(b => b.classList.toggle('selected', b === button)); renderChart();
  if (isEtf()) refreshPublicQuotes(); else publicAuto(false);
});
async function control(action) {
  try { await api(`control/${action}`, {method:'POST'}); message(action === 'flatten' ? 'Flatten requested. Check activity and positions for completion.' : 'Paper control applied.'); await refresh(); }
  catch (error) { message(error.message); }
}
document.querySelectorAll('[data-action]').forEach(b => b.onclick = () => control(b.dataset.action));
$('flatten').onclick = () => $('flatten-dialog').showModal();
$('cancel-flatten').onclick = () => $('flatten-dialog').close();
$('confirm-flatten').onclick = () => { $('flatten-dialog').close(); control('flatten'); };
function populateSettings() {
  for (const input of $('settings-form').querySelectorAll('input')) input.value = input.name.endsWith('Pct') ? state.settings[input.name] * 100 : state.settings[input.name];
}
$('settings-form').onsubmit = async event => {
  event.preventDefault(); const settings = {...state.settings};
  for (const input of $('settings-form').querySelectorAll('input')) settings[input.name] = input.type === 'time' ? input.value : Number(input.value) / (input.name.endsWith('Pct') ? 100 : 1);
  try { await api('settings', {method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(settings)}); await refresh(); populateSettings(); message('Paper settings saved.'); }
  catch (error) { message(error.message); }
};
async function loadReplays() {
  const runs = await api('replays');
  table('replays', runs.map(run => { const button = document.createElement('button'); button.textContent = 'View report'; button.onclick = async () => { try { showReplay(await api(`replays/${run.id}`)); } catch (e) { message(e.message); } }; return [run.name,date(run.createdAt),run.barsProcessed,run.tradesClosed,usd(run.netProfitLoss),button]; }), 'No saved research. Upload a licensed CSV to run your first replay.', 6);
}
$('replay-form').onsubmit = async event => {
  event.preventDefault(); const file = $('csv').files[0]; if (!file) return;
  if (file.size > 2 * 1024 * 1024) { message('CSV exceeds the 2 MB upload limit.'); return; }
  $('run-replay').disabled = true; text('run-replay','Running replay…');
  try {
    const result = await api('replays',{method:'POST',headers:{'Content-Type':'text/csv','X-File-Name':encodeURIComponent(file.name)},body:file});
    showReplay(result); await loadReplays(); message('Replay completed and saved locally. Forward paper equity is unchanged.');
  } catch (error) { message(error.message); }
  finally { $('run-replay').disabled = false; text('run-replay','Run paper replay →'); }
};
function showReplay(run) {
  currentReplay = run; $('replay-detail').hidden = false; text('replay-title',run.name); text('replay-assumptions',run.assumptions);
  const report = run.report; $('replay-metrics').replaceChildren();
  for (const [label,value] of [['Net P/L',usd(report.netProfitLoss)],['Ending equity',usd(report.endingEquity)],['Closed trades',report.tradesClosed],['Win rate',percent(report.winRate)],['Profit factor',report.profitFactor === null ? 'N/A' : number(report.profitFactor)]]) {
    const box = document.createElement('div'), small = document.createElement('small'), strong = document.createElement('strong'); small.textContent = label; strong.textContent = value; box.append(small,strong); $('replay-metrics').append(box);
  }
  table('replay-trades',report.closedTrades.map(t => [t.symbol,number(t.entryPrice),number(t.exitPrice),usd(t.netProfitLoss),date(t.closedAtUtc)]),'No trades closed in this replay.',5);
}
$('download-report').onclick = () => {
  if (!currentReplay) return; const url = URL.createObjectURL(new Blob([JSON.stringify(currentReplay,null,2)],{type:'application/json'}));
  const link = document.createElement('a'); link.href = url; link.download = `paper-replay-${currentReplay.id}.json`; link.click(); setTimeout(() => URL.revokeObjectURL(url),1000);
};
let marketChart;
function renderChart() {
  if (!state || $('overview').hidden) return;
  $('overview-public-controls').hidden = !isEtf();
  renderOverviewNotice();
  if (isEtf()) { renderEtfChart(); return; }
  text('page-title', 'Paper overview');
  text('page-description', 'Your strategy, exposure, and execution in one place.');
  text('chart-empty-message', 'Connect your MES/MNQ API feed in Market data. Historical candles and incoming trades will appear here.');
  const instrument = state.instruments.find(i => i.symbol === selectedSymbol);
  text('contract-label',instrument.contractId || 'No contract connected');
  text('quote',instrument.quote ? `${number(instrument.quote.bid)} / ${number(instrument.quote.ask)}` : '—');
  text('quote-state',instrument.quote ? instrument.fresh ? 'CURRENT API QUOTE' : 'STALE' : 'NO DATA');
  text('ema',`EMA20 ${instrument.fastEma === null ? '—' : number(instrument.fastEma)} / EMA50 ${instrument.slowEma === null ? '—' : number(instrument.slowEma)}`);
  text('quote-time',`Last quote: ${time(instrument.quote?.timestamp)}`); text('signal-reason',instrument.signalReason);
  const bars = [...instrument.bars], forming = providerState?.connection?.formingBars?.find(bar => bar.symbol === selectedSymbol);
  if (forming && (!bars.length || new Date(forming.timestamp) > new Date(bars.at(-1).timestamp))) bars.push(forming);
  text('chart-source',`${providerState?.settings.provider === 't4-simulator' ? 'T4 SIMULATOR' : 'EXTERNAL ADAPTER'} · UTC · ${forming ? 'LAST CANDLE FORMING' : 'COMPLETED BARS'}`);
  $('chart-empty').hidden = bars.length > 0;
  try { marketChart ||= new MarketChart($('chart')); marketChart.update(bars,selectedSymbol); }
  catch { text('chart-ohlc','Chart could not load. Refresh the page or inspect the local server assets.'); }
}
function renderOverviewNotice() {
  if (!providerState) return;
  if ($('overview').hidden || !isEtf()) {
    text('provider-status', providerState.status); text('provider-detail', providerState.detail);
    text('provider-tag', providerState.settings.provider === 'external' && providerState.configured ? 'INGRESS READY' : 'SETUP');
    return;
  }
  const available = publicDataState?.status === 'snapshot';
  text('provider-status', available ? 'Public.com connected · ETF market view' : 'Public.com ETF market view');
  text('provider-detail', `${publicDataState?.message || 'Load ETF quotes and historical candles using your configured Public.com connection.'} MES/MNQ paper execution uses its separately configured futures feed.`);
  text('provider-tag', 'ETF RESEARCH');
}
function renderEtfChart() {
  const quote = publicDataState?.quotes?.find(q => q.symbol === selectedSymbol);
  const history = publicHistory.get(selectedSymbol), bars = history?.bars || [];
  text('page-title', 'Market overview');
  text('page-description', 'Public.com ETF quotes and historical candles. Futures paper-account metrics appear below.');
  text('contract-label', `${selectedSymbol} · ETF · Public.com`);
  text('quote', quote?.last != null ? number(quote.last) : '—');
  text('quote-state', quote?.last != null ? quote.lastFresh ? 'CURRENT LAST TRADE' : 'STALE LAST TRADE' : 'NO QUOTE');
  const price = (value, fresh) => value == null ? '—' : `${number(value)}${fresh ? '' : ' (stale)'}`;
  text('ema', `Bid ${price(quote?.bid, quote?.bidFresh)} / Ask ${price(quote?.ask, quote?.askFresh)}`);
  text('quote-time', quote?.lastAt ? `Last trade: ${date(quote.lastAt)}` : 'Last trade timestamp unavailable');
  text('chart-source', `PUBLIC.COM · ETF · REGULAR SESSION · UTC · ${bars.length} BARS`);
  text('signal-reason', `${history?.message || 'Refresh to load the past week of five-minute candles.'} ETF research does not generate MES/MNQ signals or fills.`);
  text('chart-empty-message', history?.message || 'Refresh market view to load Public.com historical candles.');
  $('chart-empty').hidden = bars.length > 0;
  try { marketChart ||= new MarketChart($('chart')); marketChart.update(bars, selectedSymbol, {timestampMode:'start',minMove:.01}); }
  catch { text('chart-ohlc', 'Chart could not load. Refresh the page or inspect the local server assets.'); }
}
window.addEventListener('resize',renderChart);

function listText(id, values) {
  $(id).replaceChildren();
  values.forEach(value => { const item = document.createElement('li'); item.textContent = value; $(id).append(item); });
}
function renderMarketData(provider) {
  providerState = provider;
  text('provider-status', provider.status); text('provider-detail', provider.detail);
  text('provider-tag', provider.settings.provider === 'external' && provider.configured ? 'INGRESS READY' : 'SETUP');
  text('data-source-name', provider.name);
  const stream = provider.connection;
  text('data-stream-summary', stream?.message || 'Disconnected');
  text('data-stream-messages', number(stream?.messages || 0));
  text('data-stream-quotes', number(stream?.quotes || 0));
  text('data-stream-bars', number(stream?.completedBars || 0));
  text('data-stream-last', stream?.lastMessageAt ? `Last provider message: ${date(stream.lastMessageAt)} · connection attempt ${stream.attempt}` : 'No provider messages received.');
  const authentication = provider.authentication;
  text('t4-key-status', authentication?.method === 'api-key' ? 'API key configured'
    : authentication?.method === 'username-password' ? (authentication.configured ? 'Login + license configured' : 'Login + license · incomplete') : 'Not set');
  text('adapter-key-status', provider.adapterKeyPresent ? 'Present' : 'Not set');
  text('data-readiness', provider.configured ? 'SETTINGS READY' : 'INCOMPLETE');
  listText('data-missing', provider.missing.length ? provider.missing : ['Required settings are present. Check the actual data source before paper testing.']);
  text('data-test-outcome', provider.testing ? 'TESTING' : provider.lastTest?.outcome.toUpperCase() || 'NOT TESTED');
  text('data-test-summary', provider.lastTest?.summary || (provider.testing ? 'Checking simulator login, contracts and quotes…' : 'No T4 check completed for these settings in this server session.'));
  text('data-test-time', provider.lastTest ? `Checked ${date(provider.lastTest.checkedAt)} · point-in-time result` : '');
  listText('data-test-markets', (provider.lastTest?.markets || []).map(m => `${m.symbol}: ${m.status}`));
  updateMarketDataButtons();
}
function populateMarketData() {
  $('data-provider').value = providerState.settings.provider;
  $('history-timezone').value = providerState.settings.historyTimeZone || 'unconfirmed';
  $('market-bindings').replaceChildren();
  providerState.settings.instruments.forEach(market => {
    const group = document.createElement('fieldset'), legend = document.createElement('legend');
    group.dataset.market = market.symbol; legend.textContent = market.symbol; group.append(legend);
    const toggle = document.createElement('label'), checkbox = document.createElement('input');
    toggle.className = 'market-toggle'; checkbox.type = 'checkbox'; checkbox.name = 'enabled'; checkbox.checked = market.enabled;
    toggle.append(checkbox, document.createTextNode('Enable this instrument')); group.append(toggle);
    for (const [name, title, placeholder] of [['exchangeId','T4 exchange ID','Exact exchange ID'],['productId','T4 product / contract ID','Exact product ID'],['marketId','Expiring market ID','Exact provider market ID']]) {
      const label = document.createElement('label'), input = document.createElement('input');
      label.textContent = title; input.name = name; input.value = market[name]; input.maxLength = 100;
      input.placeholder = placeholder; input.autocomplete = 'off'; input.spellcheck = false;
      if (name !== 'marketId') label.className = 't4-field';
      label.append(input); group.append(label);
    }
    $('market-bindings').append(group);
  });
  marketDataDirty = false; text('data-form-status','No unsaved changes.'); updateMarketDataFields();
}
function updateMarketDataFields() {
  const provider = $('data-provider').value;
  text('data-provider-help', provider === 't4-simulator' ? 'Continuous simulator feed. Configure server credentials (API key or login with an application license) and exact T4 IDs, save, then connect. Feed connection never resumes paper entries automatically.' : provider === 'external' ? 'Your external adapter must supply authorized quotes and completed bars. The contract ID in each feed request must match the market ID below.' : 'Data ingress is disabled until a provider is selected and configured.');
  $('history-timezone-label').hidden = provider !== 't4-simulator';
  $('market-bindings').hidden = provider === 'none';
  document.querySelectorAll('.t4-field').forEach(label => label.hidden = provider !== 't4-simulator');
  document.querySelectorAll('[data-market]').forEach(group => {
    const enabled = group.querySelector('[name=enabled]').checked;
    group.querySelectorAll('input:not([type=checkbox])').forEach(input => input.disabled = !enabled);
  });
  updateMarketDataButtons();
}
function updateMarketDataButtons() {
  const active = providerState?.connection?.active;
  $('market-data-form').querySelectorAll('fieldset, select').forEach(input => input.disabled = marketDataBusy || providerState?.testing || active);
  $('save-market-data').disabled = marketDataBusy || active || providerState?.testing || !marketDataDirty || !state?.paused || state.positions.length > 0 || state.pending.length > 0;
  $('test-market-data').disabled = marketDataBusy || active || providerState?.testing || marketDataDirty || !providerState?.configured || providerState.settings.provider !== 't4-simulator' || !state?.paused || state.positions.length > 0;
  $('connect-market-data').disabled = marketDataBusy || active || providerState?.testing || marketDataDirty || !providerState?.configured || providerState.settings.provider !== 't4-simulator' || !state?.paused;
  $('disconnect-market-data').disabled = marketDataBusy || !active;
}
$('market-data-form').oninput = () => {
  marketDataDirty = true; text('data-form-status','Unsaved changes. Save before testing; the current connection settings have not changed.'); updateMarketDataFields();
};
$('market-data-form').onsubmit = async event => {
  event.preventDefault();
  const settings = {provider:$('data-provider').value, historyTimeZone:$('history-timezone').value, instruments:[...document.querySelectorAll('[data-market]')].map(group => ({
    symbol:group.dataset.market, enabled:group.querySelector('[name=enabled]').checked,
    ...Object.fromEntries(['exchangeId','productId','marketId'].map(name => [name,group.querySelector(`[name=${name}]`).value.trim()]))
  }))};
  marketDataBusy = true; updateMarketDataButtons();
  try {
    renderMarketData(await api('market-data',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(settings)}));
    populateMarketData(); await refresh(); message('Market-data settings saved. Fresh quotes and indicator warmup are required before resuming.');
  } catch (error) { message(error.message); }
  finally { marketDataBusy = false; updateMarketDataButtons(); }
};
$('test-market-data').onclick = async () => {
  marketDataBusy = true; updateMarketDataButtons(); text('data-test-summary','Checking simulator access… This takes up to 15 seconds.');
  try { renderMarketData(await api('market-data/test',{method:'POST'})); }
  catch (error) { message(error.message); }
  finally { marketDataBusy = false; updateMarketDataButtons(); await refresh(); }
};
for (const action of ['connect','disconnect']) $(action+'-market-data').onclick = async () => {
  marketDataBusy = true; updateMarketDataButtons();
  try { renderMarketData(await api('market-data/'+action,{method:'POST'})); await refresh(); }
  catch (error) { message(error.message); }
  finally { marketDataBusy = false; updateMarketDataButtons(); }
};

function renderPublicData(data) {
  if (!data) return;
  publicDataState = data;
  text('public-key-status', data.secretPresent ? 'Present on server' : 'Not configured');
  text('public-account', data.accountLabel || 'Not checked');
  text('public-response-time', data.lastSuccessAt ? date(data.lastSuccessAt) : '—');
  text('public-data-status', data.status.replaceAll('-', ' ').toUpperCase());
  text('public-data-message', data.message);
  const cooldown = data.nextRequestAt && new Date(data.nextRequestAt).getTime() > Date.now();
  text('public-retry-time', cooldown ? `Next request allowed after ${time(data.nextRequestAt)}.` : '');
  $('public-refresh').disabled = !data.secretPresent || publicRefreshBusy || data.refreshing || cooldown;
  $('public-refresh').textContent = publicRefreshBusy || data.refreshing ? 'Requesting quotes…' : 'Refresh Public.com quotes';
  $('public-auto').disabled = !data.secretPresent;
  $('overview-public-refresh').disabled = !data.secretPresent || publicRefreshBusy || data.refreshing || cooldown;
  $('overview-public-refresh').textContent = publicRefreshBusy ? 'Loading market data…' : 'Refresh market view';
  $('overview-public-auto').disabled = !data.secretPresent;
  const price = (value, fresh) => value == null ? '—' : `${number(value)}${fresh ? '' : ' · stale'}`;
  const stamp = value => value ? date(value) : 'Unknown';
  table('public-quotes', (data.quotes || []).map(q => [q.symbol,
    price(q.last, q.lastFresh), stamp(q.lastAt), price(q.bid, q.bidFresh), stamp(q.bidAt),
    price(q.ask, q.askFresh), stamp(q.askAt)]), 'Refresh to request Public.com ETF snapshots. No prices are generated.', 7);
  if (!['ready','refreshing','snapshot'].includes(data.status)) publicAuto(false);
  if (isEtf()) renderChart();
}
async function refreshPublicQuotes() {
  if (!accessKey || publicRefreshBusy || !publicDataState?.secretPresent) return;
  const requestKey = accessKey;
  let requestedSymbol;
  publicRefreshBusy = true; renderPublicData(publicDataState);
  try {
    const result = await api('market-data/public/refresh', {method:'POST'});
    if (accessKey !== requestKey) return;
    renderPublicData(result);
    if (isEtf() && !$('overview').hidden && !document.hidden && result.status === 'snapshot') {
      requestedSymbol = selectedSymbol;
      const history = await api(`market-data/public/history/${requestedSymbol}/refresh`, {method:'POST'});
      if (accessKey !== requestKey) return;
      publicHistory.set(requestedSymbol, history); renderChart();
      if (!['available','refreshing','ready'].includes(history.status)) publicAuto(false);
    }
  }
  catch (error) {
    publicAuto(false); message(error.message);
    if (accessKey === requestKey) renderPublicData({...publicDataState,status:'unavailable',message:'Public.com refresh could not be verified. Refresh to retry.',quotes:[]});
  }
  finally { publicRefreshBusy = false; publicAutoNext = Date.now() + 15000; if (publicDataState && accessKey) renderPublicData(publicDataState); }
  if (accessKey === requestKey && isEtf() && requestedSymbol && requestedSymbol !== selectedSymbol && !$('overview').hidden && !document.hidden) refreshPublicQuotes();
}
$('public-refresh').onclick = refreshPublicQuotes;
$('overview-public-refresh').onclick = refreshPublicQuotes;
for (const id of ['public-auto','overview-public-auto']) $(id).onchange = () => { publicAuto($(id).checked); if ($(id).checked) refreshPublicQuotes(); };
document.addEventListener('visibilitychange', () => { if (document.hidden) publicAuto(false); });
setInterval(() => {
  if ($('public-auto').checked && !document.hidden && (!$('market-data').hidden || !$('overview').hidden && isEtf()) && Date.now() >= publicAutoNext
      && (!publicDataState?.nextRequestAt || Date.now() >= new Date(publicDataState.nextRequestAt).getTime())) refreshPublicQuotes();
}, 1000);
