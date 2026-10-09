const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Exercise the real dashboard controller with isolated DOM/network stand-ins.
class Element {
  constructor() { this.children=[]; this.dataset={}; this.value=''; this.hidden=false; this.checked=false;
    this.classList={toggle() {},add() {},remove() {}}; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children=nodes; }
  insertRow() { const row=new Element(); this.append(row); return row; }
  insertCell() { const cell=new Element(); this.append(cell); return cell; }
  setAttribute() {} removeAttribute() {}
  descendants() { return this.children.flatMap(child => [child,...child.descendants()]); }
  querySelector(selector) { return this.descendants().find(node => selector === `[name=${node.name}]`); }
  querySelectorAll(selector) { return selector === 'input:not([type=checkbox])'
    ? this.descendants().filter(node => node.name && node.type !== 'checkbox') : []; }
}
const html=fs.readFileSync(path.join(__dirname,'../src/AutopilotQuant.Web/wwwroot/index.html'),'utf8');
const elements=new Map();
for (const match of html.matchAll(/<[^>]+\bid="([^"]+)"[^>]*>/g)) {
  const node=new Element(); node.id=match[1]; node.hidden=/\bhidden\b/.test(match[0]); elements.set(node.id,node);
}
const symbols=['SPY','QQQ','MES','MNQ'].map(symbol => Object.assign(new Element(),{dataset:{symbol}}));
const panels=['overview','research','market-data','tradingview','risk'];
const navigation=panels.map(panel => Object.assign(new Element(),{dataset:{panel}}));
const listeners={}, intervals=[], requests=[], chartUpdates=[];
const document={hidden:false, getElementById(id) { assert(elements.has(id),`Missing HTML element ${id}`); return elements.get(id); },
  createElement() { return new Element(); }, createTextNode() { return new Element(); },
  addEventListener(name,callback) { listeners[name]=callback; },
  querySelectorAll(selector) {
    if (selector==='[data-symbol]') return symbols;
    if (selector==='[data-panel]') return navigation;
    if (selector==='.panel-page') return panels.map(id => elements.get(id));
    if (selector==='[data-market]') return elements.get('market-bindings').children;
    return [];
  }};
const instruments=['MES','MNQ'].map(symbol => ({symbol,enabled:true,exchangeId:'',productId:'',marketId:'',bars:[],fresh:false,quote:null,fastEma:null,slowEma:null,signalReason:'Waiting for futures'}));
let quoteView={secretPresent:true,status:'ready',message:'Ready',quotes:[]};
const fixture={session:{serverTime:'2026-10-04T15:00:00Z',equity:10000,realizedNet:0,unrealizedGross:0,drawdownPct:0,
  settings:{maxDrawdownPct:.1,dailyLossLimitPct:.03},paused:true,riskHalted:false,pauseReason:'Waiting',dailyLossPct:0,fees:0,
  positions:[],pending:[],protection:[],fills:[],events:[],instruments},
  provider:{configured:false,name:'T4 simulator',status:'Futures setup incomplete',detail:'Configure futures access',missing:['Credentials required'],
    authentication:{method:'not-configured'},settings:{provider:'t4-simulator',historyTimeZone:'unconfirmed',instruments}}};
const bars=[{timestamp:'2026-10-02T13:30:00Z',open:500.01,high:501.01,low:499.01,close:500.11,volume:100}];
let currentTime=Date.parse('2026-10-04T15:00:00Z');
class Clock extends Date { static now() { return currentTime; } }
const context=vm.createContext({document,Node:Element,Intl,Date:Clock,console,URL,Blob,
  window:{addEventListener() {}},setInterval(callback) { intervals.push(callback); },setTimeout,
  TradingViewReference:{ReferenceChart:class { close() {} }},
  MarketChart:class { update(bars,symbol,options={}) { chartUpdates.push({bars,symbol,options}); } },
  async fetch(url,options) {
    requests.push({url,options}); let body;
    if (url==='/api/state') body={...fixture,publicData:quoteView};
    else if (url==='/api/replays') body=[];
    else if (url==='/api/market-data/public/refresh') body=quoteView={secretPresent:true,status:'snapshot',message:'Provider snapshot',quotes:
      ['SPY','QQQ'].map(symbol => ({symbol,last:500.12,lastAt:'2026-10-02T20:00:00Z',lastFresh:false,bid:500.1,bidFresh:false,ask:500.2,askFresh:false}))};
    else if (/^\/api\/market-data\/public\/history\/(SPY|QQQ)\/refresh$/.test(url)) body={symbol:url.split('/')[5],status:'available',message:'Provider historical candles',bars};
    else throw new Error(`Unexpected outbound operation ${url}`);
    return {ok:true,status:200,async json() { return body; }};
  }});
vm.runInContext(fs.readFileSync(path.join(__dirname,'../src/AutopilotQuant.Web/wwwroot/app.js'),'utf8'),context);
const el=id=>elements.get(id);
const flush=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
  el('access-key').value='test-local-dashboard-key';
  await el('unlock-form').onsubmit({preventDefault(){}});
  assert.equal(chartUpdates.at(-1).symbol,'SPY');
  assert.equal(chartUpdates.at(-1).options.timestampMode,'start');
  assert.equal(chartUpdates.at(-1).options.minMove,.01);
  assert.equal(chartUpdates.at(-1).bars.length,1);
  assert.equal(el('public-auto').checked,true);
  assert.equal(el('resume').disabled,true);
  assert.match(el('quote-state').textContent,/STALE/);
  assert.match(el('provider-tag').textContent,/ETF RESEARCH/);
  console.log('PASS: Configured Public connection opens on ETF history while stale quotes cannot arm futures');

  currentTime+=16000;
  const before=requests.length; intervals[1](); await flush();
  assert(requests.length>before);
  symbols[1].onclick(); await flush();
  assert.equal(chartUpdates.at(-1).symbol,'QQQ');
  assert.equal(chartUpdates.at(-1).bars.length,1);
  console.log('PASS: Visible ETF overview refreshes quotes and requests the selected ETF history');

  symbols[2].onclick(); await flush();
  assert.equal(chartUpdates.at(-1).symbol,'MES');
  assert.equal(chartUpdates.at(-1).bars.length,0);
  assert.equal(chartUpdates.at(-1).options.timestampMode,undefined);
  assert.equal(el('overview-public-controls').hidden,true);
  assert.equal(el('public-auto').checked,false);
  assert.match(el('provider-status').textContent,/Futures setup/);
  console.log('PASS: Switching to MES restores its own empty futures chart without ETF substitution');

  symbols[0].onclick(); await flush();
  el('overview-public-auto').checked=true; el('overview-public-auto').onchange(); await flush();
  document.hidden=true; listeners.visibilitychange();
  const hiddenCount=requests.length; currentTime+=16000; intervals[1](); await flush();
  assert.equal(requests.length,hiddenCount);
  assert.equal(el('public-auto').checked,false);
  assert.equal(el('overview-public-auto').checked,false);
  document.hidden=false;
  navigation[1].onclick();
  el('lock').onclick();
  assert.equal(el('workspace').hidden,true);
  assert(requests.every(r=>!r.url.includes('/feed/')&&!r.url.includes('/control/')));
  console.log('PASS: Hiding, leaving or locking the view stops automatic requests and never sends feed or order commands');
})().catch(error=>{ console.error(error); process.exitCode=1; });
