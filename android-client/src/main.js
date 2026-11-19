import { SunmiPrinter } from '@kduma-autoid/capacitor-sunmi-printer';
import './style.css';

const app = document.querySelector('#app');
const savedUrl = localStorage.getItem('restaurantorders-server-url') || '';

app.innerHTML = `
  <header class="topbar"><span class="mark">🍽</span><strong>RestaurantOrders</strong><span class="device">ANDROID POS</span></header>
  <section class="connection" id="connection">
    <div class="eyebrow">ΣΥΝΔΕΣΗ ΚΑΤΑΣΤΗΜΑΤΟΣ</div>
    <h1>Σύνδεση με το σύστημα</h1>
    <p>Συνδέσου στο Cloud POS του καταστήματος. Για δικτυακό ESC/POS, το Android στέλνει την εκτύπωση στον εκτυπωτή του τοπικού Wi‑Fi.</p>
    <form id="connectForm">
      <label for="serverUrl">Διεύθυνση server</label>
      <input id="serverUrl" type="url" inputmode="url" placeholder="https://your-cloud-address/pos.html" value="${escapeAttr(savedUrl)}" required />
      <button class="primary" type="submit">Σύνδεση</button>
    </form>
    <div class="printer-card"><span class="printer-icon">▤</span><div><strong>Ενσωματωμένος εκτυπωτής SUNMI</strong><small id="printerStatus">Προαιρετικός, μόνο σε συσκευές SUNMI</small></div><button class="secondary" id="testPrinter" type="button">Δοκιμή SUNMI</button></div>
    <div id="notice" class="notice" role="status"></div>
  </section>
  <section class="webapp hidden" id="webapp"><iframe id="restaurantFrame" title="RestaurantOrders"></iframe></section>
`;

const connection = document.querySelector('#connection');
const frame = document.querySelector('#restaurantFrame');
const notice = document.querySelector('#notice');
const printerStatus = document.querySelector('#printerStatus');
const pending = new Map();
let activeServerOrigin = '';
let printerReady = false;

function escapeAttr(value) {
  return String(value).replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
}

function showNotice(message, isError = false) {
  notice.textContent = message;
  notice.classList.toggle('error', isError);
  notice.classList.add('visible');
}

async function preparePrinter() {
  printerStatus.textContent = 'Σύνδεση με την υπηρεσία SUNMI…';
  await SunmiPrinter.bindService();
  await SunmiPrinter.printerInit();
  printerReady = true;
  printerStatus.textContent = 'Συνδεδεμένος και έτοιμος';
  return true;
}

document.querySelector('#connectForm').addEventListener('submit', async event => {
  event.preventDefault();
  const input = document.querySelector('#serverUrl');
  let url;
  try { url = new URL(input.value.trim()); }
  catch { showNotice('Έλεγξε τη διεύθυνση. Παράδειγμα: http://192.168.1.11:5080', true); return; }
  if (!['http:', 'https:'].includes(url.protocol)) {
    showNotice('Η διεύθυνση πρέπει να αρχίζει με http:// ή https://', true);
    return;
  }
  localStorage.setItem('restaurantorders-server-url', url.href.replace(/\/$/, ''));
  // Open the restaurant system as the top-level WebView document. Keeping it
  // in an iframe makes Android treat the server's login cookie as third-party.
  // The native bridge is retained for the embedded SUNMI printer.
  if (window.RestaurantOrdersSunmiNative) {
    try {
      const configured = JSON.parse(window.RestaurantOrdersSunmiNative.configureServer(url.href));
      if (!configured.ok) throw new Error(configured.error || 'Δεν αποθηκεύτηκε η διεύθυνση server.');
      showNotice('Σύνδεση με το σύστημα…');
      window.location.assign(url.href);
    } catch (error) {
      showNotice(error?.message || String(error), true);
    }
    return;
  }
  activeServerOrigin = url.origin;
  frame.src = url.href;
  connection.classList.add('hidden');
  document.querySelector('#webapp').classList.remove('hidden');
  showNotice('Έγινε σύνδεση. Η συσκευή είναι έτοιμη για εκτύπωση.');
  try { await preparePrinter(); }
  catch (error) {
    printerStatus.textContent = 'Δεν συνδέθηκε ο εκτυπωτής';
    showNotice(`Αποτυχία σύνδεσης SUNMI: ${error?.message || error}`, true);
  }
});

document.querySelector('#testPrinter').addEventListener('click', async () => {
  try {
    await preparePrinter();
    await SunmiPrinter.printText({ text: 'ΔΟΚΙΜΗ ΕΚΤΥΠΩΤΗ\nRestaurantOrders SUNMI\n\n' });
    await SunmiPrinter.lineWrap({ lines: 3 });
    showNotice('Η δοκιμαστική εκτύπωση στάλθηκε στον SUNMI.');
  } catch (error) {
    showNotice(`Αποτυχία δοκιμής: ${error?.message || error}`, true);
    printerStatus.textContent = 'Δεν συνδέθηκε ο εκτυπωτής';
  }
});

window.addEventListener('message', async event => {
  if (event.source !== frame.contentWindow || event.origin !== activeServerOrigin) return;
  const data = event.data;
  if (!data || data.source !== 'restaurantorders-sunmi' || !data.requestId) return;
  let response;
  try {
    if (data.action === 'prepare') {
      await preparePrinter();
      response = { ok: true };
    } else if (data.action === 'printRaw') {
      if (!printerReady) await preparePrinter();
      const copies = Math.max(1, Math.min(5, Number(data.copies) || 1));
      for (let i = 0; i < copies; i++) await SunmiPrinter.sendRAWBase64Data({ data: String(data.base64 || '') });
      response = { ok: true };
    } else {
      throw new Error('Άγνωστη εντολή εκτύπωσης.');
    }
  } catch (error) {
    response = { ok: false, error: error?.message || String(error) };
    printerReady = false;
    printerStatus.textContent = 'Δεν συνδέθηκε ο εκτυπωτής';
  }
  frame.contentWindow?.postMessage({ source: 'restaurantorders-sunmi-host', requestId: data.requestId, ...response }, event.origin);
});

if (savedUrl) {
  // Keep the connection form visible on launch so the operator can verify or change the server.
  document.querySelector('#serverUrl').value = savedUrl;
}
