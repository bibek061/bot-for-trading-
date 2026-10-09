const assert = require('node:assert/strict');
const {candles, ema} = require('../src/AutopilotQuant.Web/wwwroot/market-chart.js');

const bars = [
  {timestamp:'2026-10-05T14:05:00Z',open:100,high:102,low:99,close:101,volume:10},
  {timestamp:'2026-10-05T14:10:00Z',open:101,high:104,low:100,close:103,volume:20},
  {timestamp:'2026-10-05T14:15:00Z',open:103,high:105,low:101,close:102,volume:30},
  {timestamp:'2026-10-05T14:20:00Z',open:102,high:106,low:102,close:105,volume:40}
];
const original = JSON.stringify(bars);
const start = Date.parse('2026-10-05T14:00:00Z')/1000;
assert.deepEqual(candles(bars,15),[
  {time:start,open:100,high:105,low:99,close:102,volume:60},
  {time:start+900,open:102,high:106,low:102,close:105,volume:40}
]);
console.log('PASS: Display aggregation preserves interval boundaries, OHLC and summed volume');
assert.equal(candles(bars,5)[0].time,start);
assert.equal(JSON.stringify(bars),original);
console.log('PASS: End-stamped strategy bars plot at interval starts without mutating source bars');
assert.equal(candles(bars,5,'start')[0].time,start+300);
assert.equal(candles(bars,15,'start')[0].volume,30);
assert.throws(() => candles(bars,5,'unknown'));
console.log('PASS: Public ETF timestamps stay at provider interval starts without a futures timestamp shift');
assert.deepEqual(ema(candles(bars,5),20),[]);
assert.deepEqual(ema([{time:1,close:1},{time:2,close:2},{time:3,close:3}],2),[{time:2,value:1.5},{time:3,value:2.5}]);
console.log('PASS: EMA waits for its seed window and uses the expected smoothing');
