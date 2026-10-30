import { useEffect, useMemo, useRef, useState } from 'react'
import {
  getExpenses, createExpense, updateExpense, submitExpense,
  approveExpense, rejectExpense, deleteExpense,
} from '../api'
import { money, dateOnly, todayLocal } from '../format'
import ConfirmDialog from './ConfirmDialog'

const STATUSES = ['Draft', 'Submitted', 'Approved', 'Rejected']
const CURRENCIES = ['PKR', 'USD', 'EUR', 'GBP', 'AED']
const CATEGORIES = ['Travel', 'Meals', 'Office supplies', 'Software', 'Utilities', 'Other']

function ExpenseForm({ expense, onCancel, onSaved }) {
  const editing = !!expense
  const [title, setTitle] = useState(expense?.title || '')
  const [category, setCategory] = useState(expense?.category || '')
  const [amount, setAmount] = useState(expense ? String(expense.amount) : '')
  const [currency, setCurrency] = useState(expense?.currency || 'PKR')
  const [expenseDate, setExpenseDate] = useState(expense ? expense.expenseDate.slice(0, 10) : todayLocal())
  const [description, setDescription] = useState(expense?.description || '')
  const [errors, setErrors] = useState({})
  const [saving, setSaving] = useState(false)
  const [submitError, setSubmitError] = useState('')
  const firstRef = useRef(null)

  const currencyOptions = CURRENCIES.includes(currency) ? CURRENCIES : [currency, ...CURRENCIES]

  useEffect(() => {
    firstRef.current?.focus()
    const onKey = (e) => e.key === 'Escape' && !saving && onCancel()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [saving, onCancel])

  function validate() {
    const v = {}
    if (!title.trim()) v.title = 'Title is required.'
    else if (title.trim().length > 200) v.title = 'Use 200 characters or fewer.'
    if (!category.trim()) v.category = 'Category is required.'
    else if (category.trim().length > 100) v.category = 'Use 100 characters or fewer.'
    if (!(Number(amount) >= 0.01)) v.amount = 'Enter an amount of at least 0.01.'
    else if (Number(amount) > 999999999) v.amount = 'That amount is too large.'
    if (!expenseDate) v.expenseDate = 'Choose a date.'
    if (description.length > 500) v.description = 'Use 500 characters or fewer.'
    return v
  }

  async function handleSubmit(e) {
    e.preventDefault()
    const v = validate()
    setErrors(v)
    if (Object.keys(v).length) return

    const payload = {
      title: title.trim(),
      category: category.trim(),
      amount: Number(amount),
      currency,
      expenseDate,
      description: description.trim() || null,
    }

    setSaving(true)
    setSubmitError('')
    try {
      if (editing) await updateExpense(expense.id, payload)
      else await createExpense(payload)
      onSaved(editing ? 'updated' : 'created')
    } catch (err) {
      setSubmitError(
        err.status === 400 ? 'Could not save. Only draft expenses can be edited, and values must be valid.' : err.message,
      )
      setSaving(false)
    }
  }

  return (
    <div className="overlay" onClick={() => !saving && onCancel()}>
      <form className="card dialog wide" role="dialog" aria-modal="true" aria-labelledby="exp-title"
        onClick={(e) => e.stopPropagation()} onSubmit={handleSubmit} noValidate>
        <h3 id="exp-title">{editing ? 'Edit expense' : 'New expense'}</h3>
        <p className="muted">It is saved as a draft. Submit it when you are ready for review.</p>

        <label>
          Title
          <input ref={firstRef} value={title} onChange={(e) => setTitle(e.target.value)} aria-invalid={!!errors.title} />
          {errors.title && <span className="field-error">{errors.title}</span>}
        </label>

        <div className="form-grid">
          <label>
            Category
            <input list="expense-categories" value={category} onChange={(e) => setCategory(e.target.value)}
              aria-invalid={!!errors.category} />
            <datalist id="expense-categories">
              {CATEGORIES.map((c) => <option key={c} value={c} />)}
            </datalist>
            {errors.category && <span className="field-error">{errors.category}</span>}
          </label>

          <label>
            Date
            <input type="date" value={expenseDate} onChange={(e) => setExpenseDate(e.target.value)}
              aria-invalid={!!errors.expenseDate} />
            {errors.expenseDate && <span className="field-error">{errors.expenseDate}</span>}
          </label>

          <label>
            Amount
            <input type="number" inputMode="decimal" min="0" step="0.01" value={amount}
              onChange={(e) => setAmount(e.target.value)} placeholder="0.00" aria-invalid={!!errors.amount} />
            {errors.amount && <span className="field-error">{errors.amount}</span>}
          </label>

          <label>
            Currency
            <select value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {currencyOptions.map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </label>
        </div>

        <label>
          Description (optional)
          <textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)}
            aria-invalid={!!errors.description} />
          {errors.description && <span className="field-error">{errors.description}</span>}
        </label>

        {submitError && <p className="error banner" role="alert">{submitError}</p>}

        <div className="dialog-actions">
          <button type="button" className="btn-ghost" onClick={onCancel} disabled={saving}>Cancel</button>
          <button type="submit" className="btn-primary btn-sm" disabled={saving}>
            {saving ? <span className="spinner" /> : editing ? 'Save changes' : 'Create draft'}
          </button>
        </div>
      </form>
    </div>
  )
}

function ReviewDialog({ kind, expense, busy, onConfirm, onCancel }) {
  const [comment, setComment] = useState('')
  const ref = useRef(null)
  const approve = kind === 'approve'

  useEffect(() => {
    ref.current?.focus()
    const onKey = (e) => e.key === 'Escape' && !busy && onCancel()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [busy, onCancel])

  return (
    <div className="overlay" onClick={() => !busy && onCancel()}>
      <div className="card dialog" role="dialog" aria-modal="true" aria-labelledby="rev-title"
        onClick={(e) => e.stopPropagation()}>
        <h3 id="rev-title">{approve ? 'Approve this expense?' : 'Reject this expense?'}</h3>
        <p className="muted">
          {expense.title} for {money(expense.amount, expense.currency)}. This action is recorded in the audit log.
        </p>
        <label>
          Comment (optional)
          <textarea ref={ref} rows={3} maxLength={500} value={comment} onChange={(e) => setComment(e.target.value)} />
        </label>
        <div className="dialog-actions">
          <button className="btn-ghost" onClick={onCancel} disabled={busy}>Cancel</button>
          <button className={approve ? 'btn-primary btn-sm' : 'btn-danger'} onClick={() => onConfirm(comment.trim())} disabled={busy}>
            {busy ? <span className="spinner" /> : approve ? 'Approve' : 'Reject'}
          </button>
        </div>
      </div>
    </div>
  )
}

export default function ExpensesPage({ active, user }) {
  const [expenses, setExpenses] = useState(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [query, setQuery] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [form, setForm] = useState(null) // null | { expense? }
  const [review, setReview] = useState(null) // null | { kind, expense }
  const [toDelete, setToDelete] = useState(null)
  const [busy, setBusy] = useState(false)
  const [actionError, setActionError] = useState('')
  const [notice, setNotice] = useState('')

  const isReviewer = user.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')

  async function load() {
    setLoading(true)
    setError('')
    try {
      setExpenses(await getExpenses())
    } catch (err) {
      setError(err.message)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (active) load()
  }, [active])

  async function act(kind, expense, comment) {
    setBusy(true)
    setActionError('')
    setNotice('')
    try {
      let updated
      if (kind === 'submit') updated = await submitExpense(expense.id)
      else if (kind === 'approve') updated = await approveExpense(expense.id, comment)
      else updated = await rejectExpense(expense.id, comment)
      setExpenses((list) => list.map((e) => (e.id === expense.id ? updated : e)))
    } catch (err) {
      setActionError(err.message)
      load()
    } finally {
      setBusy(false)
      setReview(null)
    }
  }

  async function remove(expense) {
    setBusy(true)
    setActionError('')
    try {
      await deleteExpense(expense.id)
      setExpenses((list) => list.filter((e) => e.id !== expense.id))
      setNotice('Expense deleted.')
    } catch (err) {
      setActionError(err.message)
      load()
    } finally {
      setBusy(false)
      setToDelete(null)
    }
  }

  function handleSaved(kind) {
    setForm(null)
    setNotice(kind === 'created' ? 'Expense saved as a draft.' : 'Expense updated.')
    load()
  }

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (expenses || []).filter(
      (e) =>
        (!statusFilter || e.status === statusFilter) &&
        (!q || e.title.toLowerCase().includes(q) || e.category.toLowerCase().includes(q)),
    )
  }, [expenses, query, statusFilter])

  const stats = useMemo(() => {
    const list = expenses || []
    return {
      total: list.length,
      awaiting: list.filter((e) => e.status === 'Submitted').length,
      approved: list.filter((e) => e.status === 'Approved').length,
    }
  }, [expenses])

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Expenses</h2>
        <div className="head-actions">
          <button className="btn-ghost btn-sm" onClick={load} disabled={loading}>Refresh</button>
          <button className="btn-primary btn-sm" onClick={() => { setNotice(''); setForm({}) }}>New expense</button>
        </div>
      </header>

      <div className="page-body">
        {expenses && (
          <div className="stat-row">
            <div className="card stat"><span className="muted">Total expenses</span><strong>{stats.total}</strong></div>
            <div className="card stat"><span className="muted">Awaiting review</span><strong>{stats.awaiting}</strong></div>
            <div className="card stat"><span className="muted">Approved</span><strong>{stats.approved}</strong></div>
          </div>
        )}

        <div className="toolbar">
          <input type="search" placeholder="Search by title or category" value={query}
            onChange={(e) => setQuery(e.target.value)} aria-label="Search expenses" />
          <select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)} aria-label="Filter by status">
            <option value="">All statuses</option>
            {STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </select>
        </div>

        {notice && <p className="notice" role="status">{notice}</p>}
        {actionError && <p className="error banner" role="alert">{actionError}</p>}
        {error && (
          <p className="error banner" role="alert">
            {error} <button className="link-btn" onClick={load}>Try again</button>
          </p>
        )}

        {loading && !expenses && <div className="center-pad"><span className="spinner" style={{ width: 26, height: 26 }} /></div>}

        {expenses && filtered.length === 0 && (
          <div className="center-pad muted">
            {expenses.length === 0 ? 'No expenses yet. Add the first one.' : 'No expenses match your search.'}
          </div>
        )}

        {filtered.length > 0 && (
          <div className="card table-wrap">
            <table>
              <thead>
                <tr><th>Expense</th><th>Date</th><th className="num">Amount</th><th>Status</th><th /></tr>
              </thead>
              <tbody>
                {filtered.map((e) => {
                  const own = e.submittedByUserId === user.id
                  return (
                    <tr key={e.id}>
                      <td>
                        <strong>{e.title}</strong>
                        {own && isReviewer && <span className="tag">Yours</span>}
                        <span className="sub">
                          {e.category}
                          {e.reviewComment ? ` · Reviewer: ${e.reviewComment}` : ''}
                        </span>
                      </td>
                      <td>{dateOnly(e.expenseDate)}</td>
                      <td className="num">{money(e.amount, e.currency)}</td>
                      <td><span className={`badge s-${e.status.toLowerCase()}`}>{e.status}</span></td>
                      <td className="actions">
                        {e.status === 'Draft' && own && (
                          <>
                            <button className="btn-primary btn-sm" disabled={busy} onClick={() => act('submit', e)}>Submit</button>
                            <button className="btn-ghost btn-sm" disabled={busy} onClick={() => { setNotice(''); setForm({ expense: e }) }}>Edit</button>
                            <button className="btn-ghost btn-sm" disabled={busy} onClick={() => setToDelete(e)}>Delete</button>
                          </>
                        )}
                        {e.status === 'Submitted' && isReviewer && !own && (
                          <>
                            <button className="btn-primary btn-sm" disabled={busy} onClick={() => setReview({ kind: 'approve', expense: e })}>Approve</button>
                            <button className="btn-ghost btn-sm" disabled={busy} onClick={() => setReview({ kind: 'reject', expense: e })}>Reject</button>
                          </>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {form && <ExpenseForm expense={form.expense} onCancel={() => setForm(null)} onSaved={handleSaved} />}

      {review && (
        <ReviewDialog
          kind={review.kind}
          expense={review.expense}
          busy={busy}
          onConfirm={(comment) => act(review.kind, review.expense, comment)}
          onCancel={() => setReview(null)}
        />
      )}

      {toDelete && (
        <ConfirmDialog
          title="Delete this draft?"
          message={`"${toDelete.title}" will be permanently deleted.`}
          confirmLabel="Delete"
          danger
          busy={busy}
          onConfirm={() => remove(toDelete)}
          onCancel={() => setToDelete(null)}
        />
      )}
    </main>
  )
}