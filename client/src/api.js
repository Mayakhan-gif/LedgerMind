export const DEMO_MODE = false

const API_URL = import.meta.env.VITE_API_URL || 'https://localhost:7210'
const REFRESH_KEY = 'lm_refresh_token'

let accessToken = null // kept in memory only
let refreshPromise = null // only one refresh at a time
let onSessionExpired = () => {}

export class ApiError extends Error {
  constructor(message, status) {
    super(message)
    this.status = status
  }
}

export function setSessionExpiredHandler(fn) {
  onSessionExpired = fn
}

function friendlyMessage(status) {
  if (status === 401) return 'Your session has expired. Please sign in again.'
  if (status === 403) return 'You do not have permission to do that.'
  if (status === 400) return 'The request was rejected. The record may have changed, please refresh.'
  if (status === 404) return 'That record no longer exists.'
  if (status === 429) return 'Too many requests. Please wait a moment.'
  if (status === 502) return 'The AI service is not responding. Please try again shortly.'
  if (status >= 500) return 'Something went wrong on the server.'
  return 'Request failed. Please try again.'
}

function saveSession({ token, refreshToken }) {
  accessToken = token
  sessionStorage.setItem(REFRESH_KEY, refreshToken)
}

function clearSession() {
  accessToken = null
  sessionStorage.removeItem(REFRESH_KEY)
}

// Refresh tokens rotate. If two refreshes run at once, the second sends the old
// token and the backend revokes all tokens. So we share a single promise.
function refreshSession() {
  const stored = sessionStorage.getItem(REFRESH_KEY)
  if (!stored) return Promise.resolve(false)

  if (!refreshPromise) {
    refreshPromise = fetch(`${API_URL}/api/Auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: stored }),
    })
      .then(async (res) => {
        if (!res.ok) {
          clearSession()
          return false
        }
        saveSession(await res.json())
        return true
      })
      .catch(() => false) // network problem: do not clear the session
      .finally(() => {
        refreshPromise = null
      })
  }
  return refreshPromise
}

async function request(path, { method = 'GET', body, auth = true, retry = true } = {}) {
  const headers = {}
  const payload = typeof body === 'function' ? body() : body
  if (payload !== undefined) headers['Content-Type'] = 'application/json'
  if (auth && accessToken) headers.Authorization = `Bearer ${accessToken}`

  let res
  try {
    res = await fetch(`${API_URL}${path}`, {
      method,
      headers,
      body: payload !== undefined ? JSON.stringify(payload) : undefined,
    })
  } catch {
    throw new ApiError('Cannot reach the server. Is the backend running?', 0)
  }

  // Access token expired: refresh once, then retry the same request
  if (res.status === 401 && auth && retry) {
    if (await refreshSession()) {
      return request(path, { method, body, auth, retry: false })
    }
    onSessionExpired()
    throw new ApiError(friendlyMessage(401), 401)
  }

  if (!res.ok) throw new ApiError(friendlyMessage(res.status), res.status)
  if (res.status === 204) return null
  const text = await res.text()
  return text ? JSON.parse(text) : null
}

function getMe() {
  return request('/api/Auth/me')
}

export async function login(email, password) {
  try {
    saveSession(await request('/api/Auth/login', { method: 'POST', body: { email, password }, auth: false }))
  } catch (err) {
    if (err.status === 401) throw new ApiError('Incorrect email or password.', 401)
    throw err
  }

  try {
    return await getMe()
  } catch (err) {
    clearSession()
    throw err
  }
}

// On page reload: if a refresh token is stored, restore the session without signing in again
export async function restoreSession() {
  if (!sessionStorage.getItem(REFRESH_KEY)) return null
  if (!(await refreshSession())) return null
  try {
    return await getMe()
  } catch {
    return null
  }
}

export async function logout() {
  try {
    await request('/api/Auth/logout', {
      method: 'POST',
      body: () => ({ refreshToken: sessionStorage.getItem(REFRESH_KEY) }),
    })
  } catch {
    // Even if the server is unreachable, the local session must be cleared
  } finally {
    clearSession()
  }
}

// ---------- Chat ----------
// history: [{ role: 'user' | 'assistant', content: '...' }]
export async function sendMessage(text, history = []) {
  const data = await request('/api/Chat', {
    method: 'POST',
    body: { message: text, history },
  })
  return data.reply
}

// ---------- Invoices ----------
export function getInvoices() {
  return request('/api/Invoices')
}

export function createInvoice(invoice) {
  return request('/api/Invoices', { method: 'POST', body: invoice })
}

export function updateInvoice(id, invoice) {
  return request(`/api/Invoices/${id}`, { method: 'PUT', body: invoice })
}

export function submitInvoice(id) {
  return request(`/api/Invoices/${id}/submit`, { method: 'POST' })
}

export function approveInvoice(id) {
  return request(`/api/Invoices/${id}/approve`, { method: 'POST' })
}

export function rejectInvoice(id) {
  return request(`/api/Invoices/${id}/reject`, { method: 'POST' })
}

// ---------- Customers ----------
export function getCustomers() {
  return request('/api/Customers')
}

export function createCustomer(customer) {
  return request('/api/Customers', { method: 'POST', body: customer })
}

// The backend returns 204 (empty response)
export function updateCustomer(id, customer) {
  return request(`/api/Customers/${id}`, { method: 'PUT', body: customer })
}

// ---------- Tasks ----------
export function getTasks() {
  return request('/api/Tasks')
}

export function createTask(task) {
  return request('/api/Tasks', { method: 'POST', body: task })
}

export function updateTask(id, task) {
  return request(`/api/Tasks/${id}`, { method: 'PUT', body: task })
}

// The response contains the full task
export function setTaskStatus(id, status) {
  return request(`/api/Tasks/${id}/status`, { method: 'POST', body: { status } })
}

export function deleteTask(id) {
  return request(`/api/Tasks/${id}`, { method: 'DELETE' })
}

// ---------- Expenses ----------
export function getExpenses() {
  return request('/api/Expenses')
}

export function createExpense(expense) {
  return request('/api/Expenses', { method: 'POST', body: expense })
}

export function updateExpense(id, expense) {
  return request(`/api/Expenses/${id}`, { method: 'PUT', body: expense })
}

export function submitExpense(id) {
  return request(`/api/Expenses/${id}/submit`, { method: 'POST' })
}

// The backend expects a ReviewRequest body, so we always send { comment }
export function approveExpense(id, comment) {
  return request(`/api/Expenses/${id}/approve`, { method: 'POST', body: { comment: comment || null } })
}

export function rejectExpense(id, comment) {
  return request(`/api/Expenses/${id}/reject`, { method: 'POST', body: { comment: comment || null } })
}

export function deleteExpense(id) {
  return request(`/api/Expenses/${id}`, { method: 'DELETE' })
}

// ---------- Check before redirecting to an external link ----------
// Only https and trusted hosts. If the response contains an unexpected link, the browser never opens it.
function trustedUrl(url, hostOk, message) {
  let parsed
  try {
    parsed = new URL(url)
  } catch {
    throw new ApiError(message, 0)
  }
  if (parsed.protocol !== 'https:' || !hostOk(parsed.hostname)) throw new ApiError(message, 0)
  return parsed.toString()
}

// ---------- Stripe ----------
// We do not send an amount: the server works out the invoice balance itself.
export async function startStripeCheckout(invoiceId) {
  let data
  try {
    data = await request(`/api/Stripe/checkout/${invoiceId}`, { method: 'POST' })
  } catch (err) {
    if (err.status === 400)
      throw new ApiError('This invoice cannot be paid online. Only approved invoices with an unpaid balance can be paid.', 400)
    if (err.status === 502)
      throw new ApiError('Stripe did not accept the request. Please try again later.', 502)
    if (err.status === 500)
      throw new ApiError('Stripe is not configured on the server.', 500)
    throw err
  }

  return trustedUrl(
    data?.checkoutUrl,
    (h) => h === 'stripe.com' || h.endsWith('.stripe.com'),
    'Received an unexpected payment link, so it was not opened.',
  )
}

// ---------- Xero ----------
export function getXeroStatus() {
  return request('/api/Xero/status')
}

export function getXeroInvoices() {
  return request('/api/Xero/invoices')
}

export async function getXeroConnectUrl() {
  const data = await request('/api/Xero/connect-url')
  return trustedUrl(
    data?.url,
    (h) => h === 'login.xero.com',
    'Received an unexpected sign-in link, so it was not opened.',
  )
}
// ============================================================================
// MANAGER TOOLS
// ============================================================================

// Approvals queue (invoices + expenses waiting for a manager)
export const mgrApprovals = () => request("/api/Approvals/pending");

// Invoices
export const mgrInvoices = () => request("/api/Invoices");
// action: "approve" | "reject" | "reopen" | "cancel"
export const mgrInvoiceAction = (id, action, comment) =>
  request(`/api/Invoices/${id}/${action}`, {
    method: "POST",
    body: comment && comment.trim() ? { comment: comment.trim() } : {},
  });
export const mgrDeleteInvoice = (id) => request(`/api/Invoices/${id}`, { method: "DELETE" });

// Expenses: action "approve" | "reject"
export const mgrExpenseAction = (id, action, comment) =>
  request(`/api/Expenses/${id}/${action}`, {
    method: "POST",
    body: comment && comment.trim() ? { comment: comment.trim() } : {},
  });

// Manual payments
export const mgrPaymentsFor = (invoiceId) => request(`/api/Payments/invoice/${invoiceId}`);
export const mgrAddPayment = (data) => request("/api/Payments", { method: "POST", body: data });
export const mgrDeletePayment = (id) => request(`/api/Payments/${id}`, { method: "DELETE" });

// Stripe (online payments)
export const mgrAttempts = (status) =>
  request(`/api/Stripe/attempts${status ? `?status=${encodeURIComponent(status)}` : ""}`);
// action: "apply" | "dismiss"
export const mgrResolveAttempt = (id, action, note) =>
  request(`/api/Stripe/attempts/${id}/resolve`, {
    method: "POST",
    body: note && note.trim() ? { action, note: note.trim() } : { action },
  });
export const mgrRefundAttempt = (id, note) =>
  request(`/api/Stripe/attempts/${id}/refund`, {
    method: "POST",
    body: note && note.trim() ? { note: note.trim() } : {},
  });

// Reports and reminders
export const mgrReportSummary = () => request("/api/Reports/summary");
export const mgrReportOverdue = () => request("/api/Reports/overdue");
export const mgrSendReminder = (invoiceId) =>
  request(`/api/Email/invoice-reminder/${invoiceId}`, { method: "POST" });
export const deleteCustomer = (id) => request(`/api/Customers/${id}`, { method: "DELETE" });
// ============================================================================
// ADMIN + VENDORS
// ============================================================================
const qs = (params) => {
  const p = new URLSearchParams();
  Object.entries(params || {}).forEach(([k, v]) => {
    if (v !== undefined && v !== null && String(v).trim() !== "") p.set(k, String(v).trim());
  });
  const s = p.toString();
  return s ? `?${s}` : "";
};

// Users (Admin only)
export const admUsers = () => request("/api/Users");
export const admCreateUser = (data) => request("/api/Users", { method: "POST", body: data });
export const admSetRole = (id, role) =>
  request(`/api/Users/${encodeURIComponent(id)}/role`, { method: "POST", body: { role } });
export const admUnlock = (id) =>
  request(`/api/Users/${encodeURIComponent(id)}/unlock`, { method: "POST" });

// Audit and email logs
export const admAudit = (params) => request(`/api/Audit${qs(params)}`);
export const admEmailLogs = (params) => request(`/api/Email/logs${qs(params)}`);

// Jobs
export const admRunOverdue = () => request("/api/Jobs/run-overdue-check", { method: "POST" });
export const admRunCleanup = () => request("/api/Jobs/run-token-cleanup", { method: "POST" });

// Vendors
export const venList = () => request("/api/Vendors");
export const venCreate = (data) => request("/api/Vendors", { method: "POST", body: data });
export const venUpdate = (id, data) => request(`/api/Vendors/${id}`, { method: "PUT", body: data });
export const venDelete = (id) => request(`/api/Vendors/${id}`, { method: "DELETE" });