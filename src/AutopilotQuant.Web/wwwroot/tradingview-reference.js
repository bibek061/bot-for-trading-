'use strict';

// Use the iframe URL produced by TradingView's official advanced-chart embed script.
// Never execute that remote script in the authenticated dashboard's document.
const TradingViewReference = (() => {
  const symbols = Object.freeze({SPY: 'AMEX:SPY', QQQ: 'NASDAQ:QQQ'});
  const intervals = new Set(['5', '15', '60', 'D']);
  function symbolFor(selection) {
    if (!Object.hasOwn(symbols, selection)) throw new Error('Choose SPY or QQQ for the reference chart.');
    return symbols[selection];
  }
  function chartLink(selection) {
    return `https://www.tradingview.com/chart/?symbol=${encodeURIComponent(symbolFor(selection))}`;
  }
  function embedUrl(selection, interval) {
    if (!intervals.has(interval)) throw new Error('Unsupported reference chart interval.');
    const url = new URL('https://www.tradingview-widget.com/embed-widget/advanced-chart/?locale=en');
    // Only public display settings enter the external frame: no keys, local state or URLs.
    url.hash = encodeURIComponent(JSON.stringify({
      symbol: symbolFor(selection), interval, timezone: 'Etc/UTC', theme: 'dark', style: '1',
      autosize: true, width: '100%', height: '100%', allow_symbol_change: false,
      hide_side_toolbar: false, withdateranges: true, hide_volume: false, save_image: false,
      support_host: 'https://www.tradingview.com',
      utm_source: 'autopilotquant', utm_medium: 'widget_new', utm_campaign: 'advanced-chart'
    }));
    return url.toString();
  }
  class ReferenceChart {
    constructor(document) {
      this.document = document;
      this.container = document.getElementById('reference-frame');
      this.status = document.getElementById('reference-status');
      this.placeholder = document.getElementById('reference-placeholder');
      this.symbol = document.getElementById('reference-symbol');
      this.interval = document.getElementById('reference-interval');
      this.link = document.getElementById('reference-open');
      this.loadButton = document.getElementById('reference-load');
      this.closeButton = document.getElementById('reference-close');
      this.active = false;
      this.loadButton.onclick = () => this.load();
      this.closeButton.onclick = () => this.close();
      this.symbol.onchange = this.interval.onchange = () => {
        this.link.href = chartLink(this.symbol.value);
        if (this.active) this.load();
      };
    }
    load() {
      const src = embedUrl(this.symbol.value, this.interval.value);
      const frame = this.document.createElement('iframe');
      frame.title = `TradingView ${this.symbol.value} ETF reference chart`;
      // allow-same-origin refers to the remote TradingView origin, never our local origin.
      // The dashboard CSP disallows local frames; APIs also require a separate key and origin.
      frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-popups');
      frame.referrerPolicy = 'no-referrer';
      frame.src = src;
      this.container.replaceChildren(frame);
      this.container.hidden = false;
      this.placeholder.hidden = true;
      this.active = true;
      this.closeButton.disabled = false;
      this.loadButton.textContent = 'Reload chart';
      // Frame load events cannot prove that quotes are available, current, or licensed.
      this.status.textContent = `${this.symbol.value} ETF chart requested from TradingView. Check its data-delay label; this is a separate reference display.`;
    }
    close() {
      this.container.replaceChildren();
      this.container.hidden = true;
      this.placeholder.hidden = false;
      this.active = false;
      this.closeButton.disabled = true;
      this.loadButton.textContent = 'Load TradingView chart';
      this.status.textContent = 'Chart closed. Load to connect your browser to TradingView. No API key is needed.';
    }
  }
  return {ReferenceChart, embedUrl, chartLink};
})();
if (typeof module !== 'undefined') module.exports = TradingViewReference;
