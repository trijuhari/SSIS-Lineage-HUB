const puppeteer = require('puppeteer');

(async () => {
  const browser = await puppeteer.launch();
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080 });
  
  console.log('Capturing Lineage Trace...');
  await page.goto('http://localhost:5057/lineage?demo=true', { waitUntil: 'networkidle0' });
  await new Promise(r => setTimeout(r, 4000));
  
  await page.goto('http://localhost:5057/lineage-trace', { waitUntil: 'networkidle0' });
  await new Promise(r => setTimeout(r, 2000));
  
  // Try to find the input by class
  try {
    await page.type('.mud-input-slot', 'UnitPrice');
    await new Promise(r => setTimeout(r, 2000)); 
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await new Promise(r => setTimeout(r, 4000)); 
  } catch (e) {
    console.log('Could not type in autocomplete, taking empty screenshot', e.message);
  }
  
  await page.screenshot({ path: 'docs/images/lineage-trace.png' });
  await browser.close();
  console.log('Done!');
})();
