// npm install --prefix /tmp/relisten-playlist-ranks fractional-indexing@4.0.0 --ignore-scripts
// node RelistenUserServiceTests/Fixtures/playlists/generate-ranks.mjs > RelistenUserServiceTests/Fixtures/playlists/ranks.json
import { generateKeyBetween, generateNKeysBetween } from '/tmp/relisten-playlist-ranks/node_modules/fractional-indexing/src/index.js';
let seed = 719;
const random = n => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % n; };
const rows = [];
const keys = [];
for (let i = 0; i < 700; i++) {
  const at = random(keys.length + 1);
  const lower = keys[at - 1] ?? null;
  const upper = keys[at] ?? null;
  const expected = generateKeyBetween(lower, upper);
  rows.push({lower, upper, expected});
  keys.splice(at, 0, expected);
}
let upper = 'a1';
for (let i = 0; i < 300; i++) {
  const expected = generateKeyBetween('a0', upper);
  rows.push({lower: 'a0', upper, expected});
  upper = expected;
}
for (const [lower, upper] of [[null, 'a0'], ['Zz', 'a0'], ['az', null], ['bzz', null], [null, 'Z0'], [null, 'A000000000000000000000000001']]) {
  rows.push({lower, upper, expected: generateKeyBetween(lower, upper)});
}
const runs = [[null,null,5000],['a0','a1',200],[null,'a0',100],['Zz',null,100]].map(([lower,upper,count]) => ({lower,upper,count,expected:generateNKeysBetween(lower,upper,count)}));
process.stdout.write(JSON.stringify({package:'fractional-indexing@4.0.0', rows, runs}));
