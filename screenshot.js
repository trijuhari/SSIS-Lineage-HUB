const puppeteer = require('puppeteer');

(async () => {
  const browser = await puppeteer.launch();
  const page = await browser.newPage();
  await page.setViewport({ width: 1920, height: 1080 });
  
  // 1. Landing Page
  console.log('Capturing Landing Page...');
  await page.goto('http://localhost:5057/', { waitUntil: 'networkidle0' });
  // Wait a bit just in case
  await new Promise(r => setTimeout(r, 1000));
  await page.screenshot({ path: 'docs/images/landing-page.png' });

  // 2. Lineage Visualization (Demo)
  console.log('Capturing Lineage Visualization...');
  await page.goto('http://localhost:5057/lineage?demo=true', { waitUntil: 'networkidle0', timeout: 60000 });
  // The blazor page might take a few seconds to render the graph, let's wait for a specific element or just wait 5 seconds.
  await new Promise(r => setTimeout(r, 6000)); 
  await page.screenshot({ path: 'docs/images/lineage-visualization.png' });

  await browser.close();
  console.log('Done!');
})();
