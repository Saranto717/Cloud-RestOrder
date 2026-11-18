const form = document.getElementById('login-form');
const button = form.querySelector('button[type="submit"]');
const errorElement = document.getElementById('login-error');

async function request(path, method = 'GET', body) {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const message = response.status === 401
      ? 'Το email ή ο κωδικός δεν είναι σωστά.'
      : data.error || `Αποτυχία αιτήματος (${response.status}).`;
    throw Object.assign(new Error(message), { status: response.status });
  }
  return data;
}

function openRestaurantApp() {
  window.location.replace('/');
}

function showError(message) {
  errorElement.textContent = message;
  errorElement.hidden = !message;
}

form.addEventListener('submit', async event => {
  event.preventDefault();
  button.disabled = true;
  showError('');

  const values = new FormData(form);
  try {
    const user = await request('/api/auth/login', 'POST', {
      email: values.get('email'),
      password: values.get('password')
    });
    openRestaurantApp(user);
  } catch (error) {
    showError(error.message);
    button.disabled = false;
  }
});

if (location.protocol !== 'https:') {
  document.getElementById('http-warning').hidden = false;
  form.querySelectorAll('input,button').forEach(control => control.disabled = true);
} else {
  request('/api/auth/session')
    .then(openRestaurantApp)
    .catch(error => {
      if (error.status !== 401) showError(error.message);
    });
}
