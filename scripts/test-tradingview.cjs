const assert = require('node:assert/strict');
const {ReferenceChart, embedUrl, chartLink} = require('../src/AutopilotQuant.Web/wwwroot/tradingview-reference.js');

for (const [selection, symbol] of [['SPY', 'AMEX:SPY'], ['QQQ', 'NASDAQ:QQQ']]) {
  const url = new URL(embedUrl(selection, '15'));
  assert.equal(url.origin, 'https://www.tradingview-widget.com');
  assert.equal(url.pathname, '/embed-widget/advanced-chart/');
  assert.equal(url.search, '?locale=en');
  const settings = JSON.parse(decodeURIComponent(url.hash.slice(1)));
  assert.equal(settings.symbol, symbol);
  assert.equal(settings.interval, '15');
  assert.equal(settings.allow_symbol_change, false);
  assert.equal(settings.theme, 'dark');
  assert(!/key|token|password|account|localhost|127\.0\.0\.1|page-uri/i.test(url.href));
  assert.equal(new URL(chartLink(selection)).searchParams.get('symbol'), symbol);
}
console.log('PASS: External charts use fixed TradingView origins and public ETF display settings only');
for (const symbol of ['MES', 'MNQ', 'CME_MINI:MES1!', '__proto__', 'constructor', '<script>', 'https://evil.example']) {
  assert.throws(() => embedUrl(symbol, '5'));
  assert.throws(() => chartLink(symbol));
}
for (const interval of ['1S', '', null, 5, '5&key=secret']) assert.throws(() => embedUrl('SPY', interval));
console.log('PASS: Futures aliases, injected URLs and invalid intervals cannot become widget inputs');

// Minimal DOM stand-in exercises lifecycle without a browser or external network requests.
const elements = new Map();
function element() {
  return {children: [], attributes: {}, value: '', hidden: false,
    replaceChildren(...children) { this.children = children; },
    setAttribute(name, value) { this.attributes[name] = value; }};
}
const document = {
  getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
  createElement(tag) { return {...element(), tag}; }
};
document.getElementById('reference-symbol').value = 'SPY';
document.getElementById('reference-interval').value = '5';
const chart = new ReferenceChart(document);
assert.equal(chart.container.children.length, 0);
chart.loadButton.onclick();
assert.equal(chart.container.children.length, 1);
const firstFrame = chart.container.children[0];
assert.equal(firstFrame.tag, 'iframe');
assert.equal(firstFrame.referrerPolicy, 'no-referrer');
assert.equal(firstFrame.attributes.sandbox, 'allow-scripts allow-same-origin allow-popups');
assert(!firstFrame.attributes.sandbox.includes('allow-top-navigation'));
assert.equal(chart.container.hidden, false);
assert.equal(chart.placeholder.hidden, true);
assert.match(chart.status.textContent, /requested/);
console.log('PASS: Chart loads only on request inside a sandbox, without referrer or navigation privileges');
chart.symbol.value = 'QQQ';
chart.symbol.onchange();
assert.equal(chart.container.children.length, 1);
assert.notEqual(chart.container.children[0], firstFrame);
assert.equal(new URL(chart.link.href).searchParams.get('symbol'), 'NASDAQ:QQQ');
assert.equal(JSON.parse(decodeURIComponent(new URL(chart.container.children[0].src).hash.slice(1))).symbol, 'NASDAQ:QQQ');
chart.closeButton.onclick();
assert.equal(chart.container.children.length, 0);
assert.equal(chart.container.hidden, true);
assert.equal(chart.active, false);
assert.equal(chart.closeButton.disabled, true);
chart.interval.value = 'D';
chart.interval.onchange();
assert.equal(chart.container.children.length, 0);
console.log('PASS: Switching symbols replaces the frame; closing removes it and subsequent selection stays offline');
