// Headless driver: runs the sweep and saves CSV + meta + screenshot.
// usage: node driver/run.cjs "<url>" <outPrefix>
const { browser } = require('/home/schorsch/.headed-chrome');
const fs = require('fs');

(async () => {
  const url = process.argv[2];
  const out = process.argv[3] || 'results/run';
  const b = await browser();
  const p = await b.newPage();
  const errs = [];
  p.on('console', m => { if (m.type() === 'error') errs.push(m.text()); });
  p.on('pageerror', e => errs.push('PAGEERR ' + e.message));
  await p.goto(url, { waitUntil: 'networkidle0', timeout: 60000 });

  // wait for completion (sweep can take a while at large n)
  const deadline = Date.now() + 10 * 60 * 1000;
  let done = false;
  while (Date.now() < deadline) {
    done = await p.evaluate(() => window.__benchDone === true);
    if (done) break;
    await new Promise(r => setTimeout(r, 1000));
  }
  if (!done) { console.error('TIMEOUT'); }

  const csv  = await p.evaluate(() => window.__benchCsv || '');
  const meta = await p.evaluate(() => window.__benchMeta || '{}');
  fs.writeFileSync(out + '.csv', 'nParts,k,frameMs,editMs\n' + csv);
  fs.writeFileSync(out + '.meta.json', meta);
  await p.screenshot({ path: out + '.png' });
  console.log('META:', meta);
  console.log('ROWS:', csv.split('\n').filter(x => x.trim()).length);
  console.log('ERRORS:', errs.length ? errs.slice(0, 4).join(' | ') : 'none');
  await p.close(); b.disconnect();
})();
