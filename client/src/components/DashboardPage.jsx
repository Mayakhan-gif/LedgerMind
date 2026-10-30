import { useEffect, useMemo, useState } from 'react'
import { getInvoices, getExpenses, getTasks } from '../api'
import { money, dateOnly, todayUtc } from '../format'

const MAX_ROWS = 5

const isInvoiceOverdue = (i) =>
  !i.isExcluded &&
  (i.status === 'Approved' || i.status === 'Partial') &&
  dateOnly(i.dueDate) < todayUtc()

export default function DashboardPage({ active, user, onNavigate }) {
  const [invoices, setInvoices] = useState(null)
  const [expenses, setExpenses] = useState(null)
  const [tasks, setTasks] = useState(null)
  const [errors, setErrors] = useState({})
  const [loading, setLoading] = useState(false)

  const isStaff = user.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')
  const displayName = user.fullName || user.email || 'there'

  async function load() {
    setLoading(true)
    // Ek hissa fail ho to baqi phir bhi dikhein
    const [inv, exp, tsk] = await Promise.allSettled([getInvoices(), getExpenses(), getTasks()])
    setInvoices(inv.status === 'fulfilled' ? inv.value : null)
    setExpenses(exp.status === 'fulfilled' ? exp.value : null)
    setTasks(tsk.status === 'fulfilled' ? tsk.value : null)
    setErrors({
      invoices: inv.status === 'rejected' ? inv.reason.message : '',
      expenses: exp.status === 'rejected' ? exp.reason.message : '',
      tasks: tsk.status === 'rejected' ? tsk.reason.message : '',
    })
    setLoading(false)
  }

  // Page jab bhi samne aaye, taaza data
  useEffect(() => {
    if (active) load()
  }, [active])

  const overdueInvoices = useMemo(
    () =>
      (invoices || [])
        .filter(isInvoiceOverdue)
        .sort((a, b) => dateOnly(a.dueDate).localeCompare(dateOnly(b.dueDate))),
    [invoices],
  )

  const pendingInvoices = useMemo(
    () => (invoices || []).filter((i) => i.status === 'PendingApproval'),
    [invoices],
  )

  // Apne khud ke expenses approval list me nahi aate (backend ka bhi yehi usool hai)
  const pendingExpenses = useMemo(
    () => (expenses || []).filter((e) => e.status === 'Submitted' && e.submittedByUserId !== user.id),
    [expenses, user.id],
  )

  const myOpenTasks = useMemo(
    () =>
      (tasks || [])
        .filter((t) => t.status !== 'Done' && t.assignedToUserId === user.id)
        .sort((a, b) => (dateOnly(a.dueDate) === '—' ? 1 : dateOnly(b.dueDate) === '—' ? -1 : dateOnly(a.dueDate).localeCompare(dateOnly(b.dueDate)))),
    [tasks, user.id],
  )

  const overdueTasks = myOpenTasks.filter((t) => t.dueDate && dateOnly(t.dueDate) < todayUtc()).length

  const firstLoad = loading && !invoices && !expenses && !tasks

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Dashboard</h2>
        <div className="head-actions">
          <button className="btn-ghost btn-sm" onClick={load} disabled={loading}>Refresh</button>
        </div>
      </header>

      <div className="page-body">
        <div className="dash-head">
          <h1>Welcome, {displayName}</h1>
          <p className="muted">Here is what needs your attention today.</p>
        </div>

        {firstLoad && <div className="center-pad"><span className="spinner" style={{ width: 26, height: 26 }} /></div>}

        {!firstLoad && (
          <>
            <div className="stat-row">
              <button className="card stat stat-link" onClick={() => onNavigate('invoices')}>
                <span className="muted">Overdue invoices</span>
                <strong>{invoices ? overdueInvoices.length : '—'}</strong>
              </button>
              {isStaff && (
                <button className="card stat stat-link" onClick={() => onNavigate('invoices')}>
                  <span className="muted">Invoices to approve</span>
                  <strong>{invoices ? pendingInvoices.length : '—'}</strong>
                </button>
              )}
              {isStaff && (
                <button className="card stat stat-link" onClick={() => onNavigate('expenses')}>
                  <span className="muted">Expenses to review</span>
                  <strong>{expenses ? pendingExpenses.length : '—'}</strong>
                </button>
              )}
              <button className="card stat stat-link" onClick={() => onNavigate('tasks')}>
                <span className="muted">My open tasks</span>
                <strong>{tasks ? myOpenTasks.length : '—'}</strong>
                {overdueTasks > 0 && <span className="sub dash-error">{overdueTasks} overdue</span>}
              </button>
            </div>

            <div className="dash-grid">
              <section className="card dash-card">
                <header>
                  <h3>Overdue invoices</h3>
                  <button className="link-btn" onClick={() => onNavigate('invoices')}>View all</button>
                </header>
                {errors.invoices ? (
                  <p className="dash-error" role="alert">{errors.invoices}</p>
                ) : overdueInvoices.length === 0 ? (
                  <p className="muted dash-empty">No overdue invoices.</p>
                ) : (
                  <ul className="dash-list">
                    {overdueInvoices.slice(0, MAX_ROWS).map((i) => (
                      <li key={i.id}>
                        <span className="item-main">
                          <strong>{i.invoiceNumber}</strong>
                          <span className="muted">{i.customer?.name || '—'} · due {dateOnly(i.dueDate)}</span>
                        </span>
                        <span className="item-side">{money(i.total, i.currency)}</span>
                      </li>
                    ))}
                  </ul>
                )}
              </section>

              {isStaff && (
                <section className="card dash-card">
                  <header>
                    <h3>Waiting for approval</h3>
                    <button className="link-btn" onClick={() => onNavigate('invoices')}>Open</button>
                  </header>
                  {errors.invoices && errors.expenses ? (
                    <p className="dash-error" role="alert">{errors.invoices}</p>
                  ) : pendingInvoices.length === 0 && pendingExpenses.length === 0 ? (
                    <p className="muted dash-empty">Nothing is waiting for you.</p>
                  ) : (
                    <ul className="dash-list">
                      {pendingInvoices.slice(0, MAX_ROWS).map((i) => (
                        <li key={`inv-${i.id}`}>
                          <span className="item-main">
                            <strong>{i.invoiceNumber}</strong>
                            <span className="muted">Invoice · {i.customer?.name || '—'}</span>
                          </span>
                          <span className="item-side">{money(i.total, i.currency)}</span>
                        </li>
                      ))}
                      {pendingExpenses.slice(0, MAX_ROWS).map((e) => (
                        <li key={`exp-${e.id}`}>
                          <span className="item-main">
                            <strong>{e.title}</strong>
                            <span className="muted">Expense · {e.category}</span>
                          </span>
                          <span className="item-side">{money(e.amount, e.currency)}</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </section>
              )}

              <section className="card dash-card">
                <header>
                  <h3>My open tasks</h3>
                  <button className="link-btn" onClick={() => onNavigate('tasks')}>View all</button>
                </header>
                {errors.tasks ? (
                  <p className="dash-error" role="alert">{errors.tasks}</p>
                ) : myOpenTasks.length === 0 ? (
                  <p className="muted dash-empty">You have no open tasks.</p>
                ) : (
                  <ul className="dash-list">
                    {myOpenTasks.slice(0, MAX_ROWS).map((t) => (
                      <li key={t.id}>
                        <span className="item-main">
                          <strong>{t.title}</strong>
                          <span className="muted">{t.priority} priority</span>
                        </span>
                        <span className="item-side">
                          {t.dueDate ? dateOnly(t.dueDate) : 'No due date'}
                          {t.dueDate && dateOnly(t.dueDate) < todayUtc() && <span className="tag tag-red">Overdue</span>}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </section>
            </div>
          </>
        )}
      </div>
    </main>
  )
}