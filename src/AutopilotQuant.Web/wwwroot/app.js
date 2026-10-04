'use strict';
const $ = id => document.getElementById(id);
let accessKey = '', state, selectedSymbol = 'MES', currentReplay, polling = false;
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
function lock() { accessKey = ''; $('workspace').hidden = true; $('login').hidden = false; $('access-key').value = ''; }
$('lock').onclick = lock;
$('unlock-form').onsubmit = async event => {
  event.preventDefault(); accessKey = $('access-key').value.trim();
  try {
    const result = await api('state');
    $('access-key').value = ''; $('login').hidden = true; $('workspace').hidden = false;
    render(result); populateSettings(); await loadReplays();
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
  text('provider-status', result.provider.configured ? result.provider.status : 'Market data not configured');
  text('provider-detail', result.provider.configured ? 'Freshness is checked for each instrument. All fills and protective exits remain simulated.' : 'Connect an authorized adapter to begin forward paper testing. No prices are generated.');
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
    text('mark-status', 'SERVER UNREACHABLE · displayed values may be stale');
    message(error.message);
  } finally { polling = false; }
}
setInterval(refresh, 3000);
document.querySelectorAll('[data-panel]').forEach(button => button.onclick = () => {
  const name = button.dataset.panel;
  document.querySelectorAll('.panel-page').forEach(panel => panel.hidden = panel.id !== name);
  document.querySelectorAll('[data-panel]').forEach(nav => { nav.classList.toggle('active',nav === button); if (nav === button) nav.setAttribute('aria-current','page'); else nav.removeAttribute('aria-current'); });
  text('breadcrumb', {overview:'Overview',research:'Replay lab',risk:'Risk & settings'}[name]);
  text('page-title', {overview:'Paper overview',research:'Replay lab',risk:'Risk & settings'}[name]);
  text('page-description', {overview:'Your strategy, exposure, and execution in one place.',research:'Turn your historical data into inspectable research.',risk:'Set boundaries before your strategy takes a position.'}[name]);
  if (name === 'overview') renderChart();
});
document.querySelectorAll('[data-symbol]').forEach(button => button.onclick = () => {
  selectedSymbol = button.dataset.symbol;
  document.querySelectorAll('[data-symbol]').forEach(b => b.classList.toggle('selected', b === button)); renderChart();
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
function renderChart() {
  if (!state || $('overview').hidden) return;
  const instrument = state.instruments.find(i => i.symbol === selectedSymbol);
  text('contract-label',instrument.contractId || 'No contract connected');
  text('quote',instrument.quote ? `${number(instrument.quote.bid)} / ${number(instrument.quote.ask)}` : '—');
  text('quote-state',instrument.quote ? instrument.fresh ? 'FRESH' : 'STALE' : 'NO DATA');
  text('ema',`EMA20 ${instrument.fastEma === null ? '—' : number(instrument.fastEma)} / EMA50 ${instrument.slowEma === null ? '—' : number(instrument.slowEma)}`);
  text('quote-time',`Last quote: ${time(instrument.quote?.timestamp)}`);
  text('signal-reason',instrument.signalReason);
  const bars = instrument.bars.slice(-65), canvas = $('chart'), ctx = canvas.getContext('2d');
  const width = canvas.clientWidth, height = canvas.clientHeight, scale = window.devicePixelRatio || 1;
  if (!width) return; canvas.width = width * scale; canvas.height = height * scale; ctx.scale(scale,scale); ctx.clearRect(0,0,width,height);
  $('chart-empty').hidden = bars.length > 0; if (!bars.length) return;
  const low = Math.min(...bars.map(b => b.low)), high = Math.max(...bars.map(b => b.high));
  const pad = Math.max((high-low)*.12,1), min = low-pad, max = high+pad, plotWidth = width-58, plotHeight = height-25;
  const y = price => 8 + (max-price)/(max-min)*(plotHeight-12);
  ctx.font = '10px Segoe UI'; ctx.fillStyle = '#7288a4'; ctx.strokeStyle = '#223042'; ctx.lineWidth = 1;
  for (let i=0;i<5;i++) { const value = min+(max-min)*i/4, py = y(value); ctx.beginPath();ctx.moveTo(0,py);ctx.lineTo(plotWidth,py);ctx.stroke();ctx.fillText(number(value),plotWidth+7,py+3); }
  const step = plotWidth/bars.length;
  bars.forEach((bar,i) => { const x = step*(i+.5), color = bar.close >= bar.open ? '#8be0c5' : '#da899a'; ctx.strokeStyle=color;ctx.fillStyle=color;ctx.beginPath();ctx.moveTo(x,y(bar.high));ctx.lineTo(x,y(bar.low));ctx.stroke();ctx.fillRect(x-Math.max(2,step*.56)/2,Math.min(y(bar.open),y(bar.close)),Math.max(2,step*.56),Math.max(1,Math.abs(y(bar.open)-y(bar.close)))); });
  ctx.fillStyle='#7288a4';ctx.fillText(time(bars[0].timestamp),0,height-3);ctx.fillText(time(bars.at(-1).timestamp),Math.max(0,plotWidth-60),height-3);
}
window.addEventListener('resize',renderChart);
