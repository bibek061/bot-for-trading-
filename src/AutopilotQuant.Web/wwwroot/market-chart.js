'use strict';
// Display calculations are separate from the server's five-minute strategy and order decisions.
const ChartMath = {
  candles(bars, minutes, timestampMode = 'end') {
    if (!['start','end'].includes(timestampMode)) throw new Error('Unknown candle timestamp convention.');
    const groups = new Map();
    for (const bar of bars) {
      const start = new Date(bar.timestamp).getTime() / 1000 - (timestampMode === 'end' ? 300 : 0);
      const time = Math.floor(start / (minutes * 60)) * minutes * 60;
      const previous = groups.get(time);
      if (previous) { previous.high = Math.max(previous.high, bar.high); previous.low = Math.min(previous.low, bar.low); previous.close = bar.close; previous.volume += bar.volume; }
      else groups.set(time, {time, open:bar.open, high:bar.high, low:bar.low, close:bar.close, volume:bar.volume});
    }
    return [...groups.values()].sort((a,b) => a.time-b.time);
  },
  ema(candles, period) {
    if (candles.length < period) return [];
    let value = candles.slice(0,period).reduce((sum,bar) => sum+bar.close,0) / period;
    const result = [{time:candles[period-1].time,value}], alpha = 2/(period+1);
    for (let i=period;i<candles.length;i++) { value += alpha*(candles[i].close-value); result.push({time:candles[i].time,value}); }
    return result;
  }
};
if (typeof module !== 'undefined') module.exports = ChartMath;

class MarketChart {
  constructor(container) {
    const L = LightweightCharts;
    this.chart = L.createChart(container, {
      autoSize:true, layout:{background:{type:'solid',color:'#111925'},textColor:'#91a1b7',fontFamily:'Segoe UI, sans-serif',attributionLogo:false},
      grid:{vertLines:{color:'#1b2838'},horzLines:{color:'#1b2838'}},
      crosshair:{mode:L.CrosshairMode.Normal,vertLine:{color:'#71869e'},horzLine:{color:'#71869e'}},
      rightPriceScale:{borderColor:'#2a3a4c',scaleMargins:{top:.1,bottom:.25}},
      timeScale:{timeVisible:true,secondsVisible:false,borderColor:'#2a3a4c',rightOffset:5},
      localization:{locale:'en-US',timeFormatter:stamp => new Date(stamp*1000).toLocaleString('en-US',{timeZone:'UTC',month:'short',day:'numeric',hour:'2-digit',minute:'2-digit',hour12:false})}
    });
    const priceFormat = {type:'price',precision:2,minMove:.25};
    this.candles = this.chart.addSeries(L.CandlestickSeries,{upColor:'#8be0c5',downColor:'#ef8a9c',wickUpColor:'#8be0c5',wickDownColor:'#ef8a9c',borderVisible:false,priceFormat});
    this.line = this.chart.addSeries(L.LineSeries,{color:'#8be0c5',lineWidth:2,visible:false,priceFormat});
    this.fast = this.chart.addSeries(L.LineSeries,{color:'#e7ba74',lineWidth:1,priceLineVisible:false,lastValueVisible:false,title:'EMA20'});
    this.slow = this.chart.addSeries(L.LineSeries,{color:'#8c9cff',lineWidth:1,priceLineVisible:false,lastValueVisible:false,title:'EMA50'});
    this.volume = this.chart.addSeries(L.HistogramSeries,{priceFormat:{type:'volume'},priceScaleId:'volume',lastValueVisible:false,priceLineVisible:false});
    this.volume.priceScale().applyOptions({scaleMargins:{top:.83,bottom:0},visible:false});
    this.cache = new Map(); this.dataset = ''; this.signature = '';
    document.getElementById('chart-fit').onclick = () => this.chart.timeScale().fitContent();
    document.getElementById('chart-latest').onclick = () => this.chart.timeScale().scrollToRealTime();
    document.getElementById('chart-expand').onclick = async () => {
      try { if (document.fullscreenElement) await document.exitFullscreen(); else await document.getElementById('market-chart-card').requestFullscreen(); }
      catch { document.getElementById('chart-ohlc').textContent = 'Fullscreen is unavailable in this browser.'; }
    };
    ['chart-interval','chart-style','chart-ema','chart-volume'].forEach(id => document.getElementById(id).onchange = () => { this.signature = ''; this.update(this.lastBars || [],this.lastSymbol || 'MES',this.lastOptions); });
    this.chart.subscribeCrosshairMove(event => {
      const bar = event.seriesData.get(document.getElementById('chart-style').value === 'line' ? this.line : this.candles);
      if (!bar || !event.time) { this.legend(this.lastCandle); return; }
      const full = this.displayBars?.find(item => item.time === event.time); this.legend(full);
    });
  }
  legend(bar) {
    document.getElementById('chart-ohlc').textContent = bar
      ? `O ${bar.open.toFixed(2)}   H ${bar.high.toFixed(2)}   L ${bar.low.toFixed(2)}   C ${bar.close.toFixed(2)}   VOL ${bar.volume.toLocaleString()}`
      : 'Move the crosshair to inspect a candle. Scroll to zoom; drag to pan.';
  }
  set(series, data) {
    const previous = this.cache.get(series);
    // Append/update the newest point without resetting a user's viewport.
    const prefix = previous && data.length >= previous.length && data.length > 0 && previous.length > 0
      && data[0].time === previous[0].time && JSON.stringify(data.slice(0,previous.length-1)) === JSON.stringify(previous.slice(0,-1));
    if (prefix) data.slice(previous.length-1).forEach(point => series.update(point));
    else series.setData(data);
    this.cache.set(series,data);
  }
  update(bars, symbol, options = {}) {
    this.lastBars = bars; this.lastSymbol = symbol; this.lastOptions = options;
    const minutes = Number(document.getElementById('chart-interval').value);
    const style = document.getElementById('chart-style').value;
    const ema = document.getElementById('chart-ema').checked, volume = document.getElementById('chart-volume').checked;
    const dataset = `${symbol}:${minutes}`;
    const signature = JSON.stringify([dataset,style,ema,volume,bars,options]);
    if (signature === this.signature) return;
    const candles = ChartMath.candles(bars, minutes, options.timestampMode || 'end');
    this.displayBars = candles; this.lastCandle = candles.at(-1);
    this.set(this.candles,candles.map(({time,open,high,low,close}) => ({time,open,high,low,close})));
    this.set(this.line,candles.map(bar => ({time:bar.time,value:bar.close})));
    this.set(this.fast,ChartMath.ema(candles,20)); this.set(this.slow,ChartMath.ema(candles,50));
    this.set(this.volume,candles.map(bar => ({time:bar.time,value:bar.volume,color:bar.close>=bar.open?'#43877788':'#a5526288'})));
    const priceFormat = {type:'price',precision:2,minMove:options.minMove || .25};
    this.candles.applyOptions({visible:style==='candles',priceFormat}); this.line.applyOptions({visible:style==='line',priceFormat});
    this.fast.applyOptions({visible:ema}); this.slow.applyOptions({visible:ema}); this.volume.applyOptions({visible:volume});
    this.chart.priceScale('right').applyOptions({scaleMargins:{top:.1,bottom:volume ? .25 : .1}});
    if (this.dataset !== dataset) this.chart.timeScale().fitContent();
    this.dataset = dataset; this.signature = signature; this.legend(this.lastCandle);
  }
}
