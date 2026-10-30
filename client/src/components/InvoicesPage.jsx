import { useEffect, useMemo, useState } from 'react'
import {
  getInvoices, submitInvoice, approveInvoice, rejectInvoice, startStripeCheckout,
} from '../api'
import { money, dateOnly, todayUtc } from '../format'
import ConfirmDialog from './ConfirmDialog'
import InvoiceForm from './InvoiceForm'
import InvoiceEditForm from './InvoiceEditForm'

const STATUSES = ['Draft', 'PendingApproval', 'Approved', 'Partial', 'Paid', 'Rejected', 'Cancelled']
const LABELS = { PendingApproval: 'Pending approval' }
const label = (s) => LABELS[s] || s

// Same rule as the backend overdue job: Approved or Partial, past the due date, not excluded
function isOverdue(inv) {
  return (
    !inv.isExcluded &&
    (inv.status === 'Approved' || inv.status === 'Partial') &&
    dateOnly(inv.dueDate) < todayUtc()
  )
}

const canPay = (inv) => !inv.isExcluded && (inv.status === 'Approved' || inv.status === 'Partial')

export default function InvoicesPage({ active, user, initialNotice = '' }) {
  const [invoices, setInvoices] = useState(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [query, setQuery] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [pending, setPending] = useState(null) // { kind, invoice }
  const [payTarget, setPayTarget] = useState(null)
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState('')
  const [showForm, setShowForm] = useState(false)
  const [editTarget, setEditTarget] = useState(null)
  const [notice, setNotice] = useState(initialNotice)

  const canReview = user.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')

  // Managers can edit any draft. Employees can edit drafts they created.
  // The server enforces this rule too, so this only controls whether the button is shown.
  const canEdit = (inv) =>
    inv.status === 'Draft' && (canReview || !user.id || !inv.createdByUserId || inv.createdByUserId === user.id)

  async function load() {
    setLoading(true)
    setError('')
    try {
      setInvoices(await getInvoices())
    } catch (err) {
      setError(err.message)
    } finally {
      setLoading(false)
    }
  }

  // Fetch fresh data whenever the page becomes active
  useEffect(() => {
    if (active) load()
  }, [active])

  async function runAction(kind, inv) {
    setBusy(true)
    setActionError('')
    setNotice('')
    try {
      const fn = { submit: submitInvoice, approve: approveInvoice, reject: rejectInvoice }[kind]
      const updated = await fn(inv.id)
      // The response does not include the customer, so only the status is updated
      setInvoices((list) => list.map((i) => (i.id === inv.id ? { ...i, status: updated.status } : i)))
    } catch (err) {
      setActionError(err.message)
      load() // the record may have been changed by someone else, so reload the list
    } finally {
      setBusy(false)
      setPending(null)
    }
  }

  async function payNow(inv) {
    setBusy(true)
    setActionError('')
    setNotice('')
    try {
      const url = await startStripeCheckout(inv.id)
      // Same tab, so the login survives the return. The page is leaving, so keep busy set.
      window.location.assign(url)
    } catch (err) {
      setActionError(err.message)
      setBusy(false)
      setPayTarget(null)
    }
  }

  function handleEdited() {
    setEditTarget(null)
    setNotice('Invoice updated.')
    load()
  }

  function handleCreated() {
    setShowForm(false)
    setNotice('Invoice created as a draft.')
    load()
  }

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (invoices || []).filter(
      (i) =>
        (!statusFilter || i.status === statusFilter) &&
        (!q || i.invoiceNumber.toLowerCase().includes(q) || (i.customer?.name || '').toLowerCase().includes(q)),
    )
  }, [invoices, query, statusFilter])

  const stats = useMemo(() => {
    const list = invoices || []
    return {
      total: list.length,
      pending: list.filter((i) => i.status === 'PendingApproval').length,
      overdue: list.filter(isOverdue).length,
    }
  }, [invoices])

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Invoices</h2>
        <div className="head-actions">
          <button className="btn-ghost btn-sm" onClick={load} disabled={loading}>Refresh</button>
          <button className="btn-primary btn-sm" onClick={() => { setNotice(''); setShowForm(true) }}>New invoice</button>
        </div>
      </header>

      <div className="page-body">
        {invoices && (
          <div className="stat-row">
            <div className="card stat"><span className="muted">Total invoices</span><strong>{stats.total}</strong></div>
            <div className="card stat"><span className="muted">Pending approval</span><strong>{stats.pending}</strong></div>
            <div className="card stat"><span className="muted">Overdue</span><strong>{stats.overdue}</strong></div>
          </div>
        )}

        <div className="toolbar">
          <input type="search" placeholder="Search by invoice number or customer" value={query}
            onChange={(e) => setQuery(e.target.value)} aria-label="Search invoices" />
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} aria-label="Filter by status">
            <option value="">All statuses</option>
            {STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}
          </select>
        </div>

        {notice && <p className="notice" role="status">{notice}</p>}
        {actionError && <p className="error banner" role="alert">{actionError}</p>}
        {error && (
          <p className="error banner" role="alert">
            {error} <button className="link-btn" onClick={load}>Try again</button>
          </p>
        )}

        {loading && !invoices && <div className="center-pad"><span className="spinner" style={{ width: 26, height: 26 }} /></div>}

        {invoices && filtered.length === 0 && (
          <div className="center-pad muted">
            {invoices.length === 0 ? 'No invoices yet. Create the first one.' : 'No invoices match your search.'}
          </div>
        )}

        {filtered.length > 0 && (
          <div className="card table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Invoice</th><th>Customer</th><th>Due date</th>
                  <th className="num">Total</th><th>Status</th><th />
                </tr>
              </thead>
              <tbody>
                {filtered.map((inv) => (
                  <tr key={inv.id}>
                    <td><strong>{inv.invoiceNumber}</strong>{inv.isExcluded && <span className="tag">Excluded</span>}</td>
                    <td>{inv.customer?.name || '—'}</td>
                    <td>{dateOnly(inv.dueDate)}{isOverdue(inv) && <span className="tag tag-red">Overdue</span>}</td>
                    <td className="num">{money(inv.total, inv.currency)}</td>
                    <td><span className={`badge s-${inv.status.toLowerCase()}`}>{label(inv.status)}</span></td>
                    <td className="actions">
                      {canEdit(inv) && (
                        <button className="btn-ghost btn-sm" disabled={busy} onClick={() => { setNotice(''); setActionError(''); setEditTarget(inv) }}>Edit</button>
                      )}
                      {inv.status === 'Draft' && (
                        <button className="btn-ghost btn-sm" disabled={busy} onClick={() => runAction('submit', inv)}>Submit</button>
                      )}
                      {inv.status === 'PendingApproval' && canReview && (
                        <>
                          <button className="btn-primary btn-sm" disabled={busy} onClick={() => setPending({ kind: 'approve', invoice: inv })}>Approve</button>
                          <button className="btn-ghost btn-sm" disabled={busy} onClick={() => setPending({ kind: 'reject', invoice: inv })}>Reject</button>
                        </>
                      )}
                      {canReview && canPay(inv) && (
                        <button className="btn-primary btn-sm" disabled={busy} onClick={() => setPayTarget(inv)}>Pay online</button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showForm && <InvoiceForm onCancel={() => setShowForm(false)} onCreated={handleCreated} />}

      {editTarget && (
        <InvoiceEditForm invoice={editTarget} onCancel={() => setEditTarget(null)} onSaved={handleEdited} />
      )}

      {pending && (
        <ConfirmDialog
          title={pending.kind === 'approve' ? 'Approve this invoice?' : 'Reject this invoice?'}
          message={`${pending.invoice.invoiceNumber} for ${money(pending.invoice.total, pending.invoice.currency)}. This action is recorded in the audit log.`}
          confirmLabel={pending.kind === 'approve' ? 'Approve' : 'Reject'}
          danger={pending.kind === 'reject'}
          busy={busy}
          onConfirm={() => runAction(pending.kind, pending.invoice)}
          onCancel={() => setPending(null)}
        />
      )}

      {payTarget && (
        <ConfirmDialog
          title="Pay this invoice online?"
          message={`${payTarget.invoiceNumber} (${money(payTarget.total, payTarget.currency)}). You will be sent to Stripe’s secure page, and the server decides the amount to charge from the unpaid balance. The invoice becomes Paid only after Stripe confirms the payment.`}
          confirmLabel="Continue to Stripe"
          busy={busy}
          onConfirm={() => payNow(payTarget)}
          onCancel={() => setPayTarget(null)}
        />
      )}
    </main>
  )
}