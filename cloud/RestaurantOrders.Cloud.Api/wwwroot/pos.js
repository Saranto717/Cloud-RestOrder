const byId = id => document.getElementById(id);
const state = { user: null, bootstrap: null, selectedTableId: null, categoryId: null, lastActivity: Date.now(), lastRefresh: Date.now(), refreshTimer: null };
const value = (object, key) => object?.[key] ?? object?.[key[0].toLowerCase() + key.slice(1)] ?? object?.[key.toLowerCase()];
const requestId = () => crypto.randomUUID();
const currency = value => new Intl.NumberFormat('el-GR', { style: 'currency', currency: 'EUR' }).format(Number(value || 0));
const showNotice = (message, isError = false) => {
  const notice = byId('app-notice'); notice.textContent = message || ''; notice.hidden = !message;
  notice.classList.toggle('error', isError);
};

async function api(path, method = 'GET', body) {
  const response = await fetch(path, { method, credentials: 'same-origin', cache: 'no-store', headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) throw Object.assign(new Error(data.error || `Αποτυχία αιτήματος (${response.status}).`), { status: response.status });
  return data;
}

function showLogin(message = '') {
  byId('pos-view').hidden = true; byId('login-view').hidden = false;
  const error = byId('login-error'); error.textContent = message; error.hidden = !message;
}

function showPos(user) {
  state.user = user; byId('login-view').hidden = true; byId('pos-view').hidden = false;
  byId('user-email').textContent = user.email; byId('staff-button').hidden = user.role !== 'admin';
  startTimers();
}

async function loadSession() {
  try { const user = await api('/api/auth/session'); showPos(user); await refreshBootstrap(); }
  catch (error) { if (error.status !== 401) showLogin(error.message); else showLogin(); }
}

async function refreshBootstrap() {
  const bootstrap = await api('/api/pos/v1/bootstrap');
  state.bootstrap = bootstrap;
  if (state.selectedTableId !== null && !tables().some(table => Number(table.entityId) === Number(state.selectedTableId))) state.selectedTableId = null;
  renderTables(); renderOrder();
}

const tables = () => state.bootstrap?.tables || [];
const menuItems = () => (state.bootstrap?.menuItems || []).filter(item => value(item.payload, 'IsAvailable') !== false);
const categories = () => state.bootstrap?.categories || [];
const orderForTable = tableId => (state.bootstrap?.orders || []).find(order => Number(value(order.payload, 'RestaurantTableId')) === Number(tableId));
const currentOrder = () => state.selectedTableId === null ? null : orderForTable(state.selectedTableId);

function renderTables() {
  const root = byId('tables'); root.replaceChildren();
  const list = tables().filter(table => value(table.payload, 'IsActive') !== false).sort((a, b) => Number(value(a.payload, 'SortOrder') || 0) - Number(value(b.payload, 'SortOrder') || 0));
  byId('table-count').textContent = `${list.length} ενεργά`;
  if (!list.length) { const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'Δεν έχουν συγχρονιστεί τραπέζια από το Windows POS.'; root.append(empty); return; }
  for (const table of list) {
    const id = Number(table.entityId); const order = orderForTable(id); const button = document.createElement('button');
    button.type = 'button'; button.className = `table-card${order ? ' occupied' : ''}${id === Number(state.selectedTableId) ? ' selected' : ''}`;
    if (order) { const dot = document.createElement('i'); dot.className = 'occupancy-dot'; button.append(dot); }
    const title = document.createElement('strong'); title.textContent = value(table.payload, 'Name') || `Τραπέζι ${id}`;
    const subtitle = document.createElement('small'); subtitle.textContent = order ? 'Ανοιχτή παραγγελία' : 'Ελεύθερο'; button.append(title, subtitle);
    button.addEventListener('click', () => selectTable(id)); root.append(button);
  }
}

async function selectTable(tableId) {
  state.selectedTableId = tableId; const activeOrder = orderForTable(tableId);
  if (!activeOrder) {
    try {
      const created = await api(`/api/pos/v1/tables/${tableId}/orders`, 'POST', { requestId: requestId() });
      await refreshBootstrap();
      state.selectedTableId = tableId;
      showNotice(`Άνοιξε νέα παραγγελία για το ${tableName(tableId)}.`);
    } catch (error) { showNotice(error.message, true); }
  }
  renderTables(); renderOrder();
}

const tableName = id => {
  const table = tables().find(item => Number(item.entityId) === Number(id));
  return value(table?.payload, 'Name') || `Τραπέζι ${id}`;
};

function renderOrder() {
  const order = currentOrder();
  byId('order-empty').hidden = !!order;
  byId('order-content').hidden = !order;
  if (!order) { byId('order-title').textContent = 'Επίλεξε τραπέζι'; byId('order-subtitle').textContent = 'Οι νέες παραγγελίες αποθηκεύονται στο cloud.'; return; }
  byId('order-title').textContent = tableName(state.selectedTableId);
  byId('order-subtitle').textContent = `Παραγγελία ${order.entityId} · Ανοιχτή`;
  const orderId = Number(order.entityId);
  const lines = (state.bootstrap?.orderItems || []).filter(item => Number(value(item.payload, 'OrderId')) === orderId && value(item.payload, 'IsCancelled') !== true);
  const lineRoot = byId('order-items'); lineRoot.replaceChildren();
  let total = 0;
  for (const line of lines) {
    const itemId = Number(value(line.payload, 'MenuItemId'));
    const product = menuItems().find(item => Number(item.entityId) === itemId);
    const name = value(product?.payload, 'Name') || `Είδος ${itemId}`;
    const quantity = Number(value(line.payload, 'Quantity') || 1);
    const price = Number(value(product?.payload, 'Price') || 0) + Number(value(line.payload, 'ExtrasPrice') || 0);
    total += price * quantity;
    const row = document.createElement('div'); row.className = 'order-line';
    const details = document.createElement('div'); const title = document.createElement('strong'); title.textContent = `${quantity} × ${name}`;
    const note = document.createElement('small'); note.textContent = value(line.payload, 'Notes') || 'Χωρίς σημείωση'; details.append(title, note);
    const amount = document.createElement('span'); amount.className = 'price'; amount.textContent = currency(price * quantity); row.append(details, amount); lineRoot.append(row);
  }
  byId('order-total').textContent = currency(total);
  renderCategories(); renderMenu();
}

function renderCategories() {
  const root = byId('categories'); root.replaceChildren();
  const all = document.createElement('button'); all.type = 'button'; all.className = `category-tab${state.categoryId === null ? ' active' : ''}`; all.textContent = 'Όλα';
  all.addEventListener('click', () => { state.categoryId = null; renderCategories(); renderMenu(); }); root.append(all);
  for (const category of categories().filter(item => value(item.payload, 'IsActive') !== false)) {
    const id = Number(category.entityId); const button = document.createElement('button'); button.type = 'button'; button.className = `category-tab${state.categoryId === id ? ' active' : ''}`; button.textContent = value(category.payload, 'Name') || 'Κατηγορία';
    button.addEventListener('click', () => { state.categoryId = id; renderCategories(); renderMenu(); }); root.append(button);
  }
}

function renderMenu() {
  const root = byId('menu-items'); root.replaceChildren();
  const filtered = menuItems().filter(item => state.categoryId === null || Number(value(item.payload, 'MenuCategoryId')) === state.categoryId);
  for (const item of filtered) {
    const button = document.createElement('button'); button.type = 'button'; button.className = 'menu-card';
    const name = document.createElement('strong'); name.textContent = value(item.payload, 'Name') || 'Προϊόν';
    const price = document.createElement('small'); price.textContent = currency(value(item.payload, 'Price')); button.append(name, price);
    button.addEventListener('click', () => addItem(item)); root.append(button);
  }
  if (!filtered.length) { const empty = document.createElement('p'); empty.className = 'notice'; empty.textContent = 'Δεν βρέθηκαν διαθέσιμα είδη.'; root.append(empty); }
}

async function addItem(item) {
  const order = currentOrder(); if (!order) return;
  const button = [...byId('menu-items').children].find(element => element.textContent.includes(value(item.payload, 'Name')));
  if (button) button.disabled = true;
  try {
    await api(`/api/pos/v1/orders/${order.entityId}/items`, 'POST', {
      requestId: requestId(), menuItemId: Number(item.entityId), quantity: 1, notes: byId('item-notes').value.trim() || null
    });
    byId('item-notes').value = '';
    await refreshBootstrap(); showNotice(`${value(item.payload, 'Name')} προστέθηκε στην παραγγελία.`);
  } catch (error) { showNotice(error.message, true); }
  finally { if (button) button.disabled = false; }
}

function startTimers() {
  if (state.refreshTimer) clearInterval(state.refreshTimer);
  state.lastActivity = Date.now(); state.lastRefresh = Date.now();
  state.refreshTimer = setInterval(async () => {
    const idleMs = Date.now() - state.lastActivity;
    if (idleMs >= 600000) { await logout(false); showLogin('Αποσυνδέθηκες μετά από 10 λεπτά αδράνειας.'); return; }
    if (idleMs < 60000 && Date.now() - state.lastRefresh >= 300000) {
      try { await api('/api/auth/refresh', 'POST', {}); state.lastRefresh = Date.now(); }
      catch { await logout(false); showLogin('Η σύνδεση έληξε. Συνδέσου ξανά.'); }
    }
  }, 15000);
}

async function logout(callServer = true) {
  if (state.refreshTimer) clearInterval(state.refreshTimer);
  state.user = null; state.bootstrap = null; state.selectedTableId = null;
  if (callServer) { try { await api('/api/auth/logout', 'POST', {}); } catch {} }
  showLogin();
}

async function loadUsers() {
  const root = byId('staff-list'); root.replaceChildren();
  const users = await api('/api/pos/v1/users');
  for (const user of users) {
    const row = document.createElement('div'); row.className = 'staff-row';
    const info = document.createElement('div'); const email = document.createElement('strong'); email.textContent = user.email;
    const role = document.createElement('small'); role.textContent = `${user.role === 'admin' ? 'Διαχειριστής' : 'Σερβιτόρος'} · ${user.isActive ? 'Ενεργός' : 'Απενεργοποιημένος'}`; info.append(email, document.createElement('br'), role); row.append(info);
    if (user.role !== 'admin' && user.isActive) {
      const deactivate = document.createElement('button'); deactivate.type = 'button'; deactivate.textContent = 'Απενεργοποίηση';
      deactivate.addEventListener('click', async () => { await api(`/api/pos/v1/users/${user.id}`, 'DELETE'); await loadUsers(); }); row.append(deactivate);
    }
    root.append(row);
  }
}

byId('login-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = new FormData(event.currentTarget); const button = event.currentTarget.querySelector('button'); button.disabled = true;
  try {
    const user = await api('/api/auth/login', 'POST', { email: form.get('email'), password: form.get('password') });
    byId('login-error').hidden = true; showPos(user); await refreshBootstrap();
  } catch (error) { byId('login-error').textContent = error.message; byId('login-error').hidden = false; }
  finally { button.disabled = false; }
});

byId('logout-button').addEventListener('click', () => logout());
byId('refresh-button').addEventListener('click', () => refreshBootstrap().catch(error => showNotice(error.message, true)));
byId('staff-button').addEventListener('click', async () => { try { await loadUsers(); byId('staff-notice').hidden = true; byId('staff-dialog').showModal(); } catch (error) { showNotice(error.message, true); } });
byId('staff-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = new FormData(event.currentTarget);
  try { await api('/api/pos/v1/users', 'POST', { email: form.get('email'), password: form.get('password'), role: form.get('role') }); event.currentTarget.reset(); await loadUsers(); }
  catch (error) { const notice = byId('staff-notice'); notice.textContent = error.message; notice.hidden = false; notice.classList.toggle('error', true); }
});

for (const activity of ['pointerdown', 'keydown', 'touchstart']) document.addEventListener(activity, () => { state.lastActivity = Date.now(); }, { passive: true });
window.addEventListener('online', () => { byId('connection-state').textContent = '● Online'; byId('connection-state').style.color = '#258456'; refreshBootstrap().catch(() => {}); });
window.addEventListener('offline', () => { byId('connection-state').textContent = '● Offline'; byId('connection-state').style.color = '#b42318'; showNotice('Δεν υπάρχει σύνδεση. Η οθόνη Android χρειάζεται Internet για νέες παραγγελίες.', true); });
setInterval(() => { if (state.user && navigator.onLine) refreshBootstrap().catch(error => { if (error.status === 401) logout(false); }); }, 12000);

if (location.protocol !== 'https:') {
  byId('http-warning').hidden = false;
  byId('login-form').querySelectorAll('input,button').forEach(control => control.disabled = true);
}
loadSession();
