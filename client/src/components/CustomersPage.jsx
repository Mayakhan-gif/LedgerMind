import { useEffect, useMemo, useRef, useState } from 'react'
import { getCustomers, createCustomer, updateCustomer, deleteCustomer } from '../api'

const dateOnly = (s) => (s ? s.slice(0, 10) : '—')

function CustomerForm({ customer, onCancel, onSaved }) {
  const editing = !!customer
  const [name, setName] = useState(customer?.name || '')
  const [email, setEmail] = useState(customer?.email || '')
  const [phone, setPhone] = useState(customer?.phone || '')
  const [errors, setErrors] = useState({})
  const [saving, setSaving] = useState(false)
  const [submitError, setSubmitError] = useState('')
  const firstRef = useRef(null)

  useEffect(() => {
    firstRef.current?.focus()
    const onKey = (e) => e.key === 'Escape' && !saving && onCancel()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [saving, onCancel])

  function validate() {
    const v = {}
    if (!name.trim()) v.name = 'Name is required.'
    if (email.trim() && !/^\S+@\S+\.\S+$/.test(email.trim())) v.email = 'Enter a valid email address.'
    return v
  }

  async function handleSubmit(e) {
    e.preventDefault()
    const v = validate()
    setErrors(v)
    if (Object.keys(v).length) return

    const payload = {
      name: name.trim(),
      email: email.trim() || null,
      phone: phone.trim() || null,
    }

    setSaving(true)
    setSubmitError('')
    try {
      if (editing) {
        await updateCustomer(customer.id, payload)
        onSaved({ ...customer, ...payload }, 'updated')
      } else {
        const created = await createCustomer(payload)
        onSaved(created, 'created')
      }
    } catch (err) {
      setSubmitError(
        err.status === 400 ? 'Could not save. Please check the values and try again.' : err.message,
      )
      setSaving(false)
    }
  }

  return (
    <div className="overlay" onClick={() => !saving && onCancel()}>
      <form className="card dialog" role="dialog" aria-modal="true" aria-labelledby="cust-title"
        onClick={(e) => e.stopPropagation()} onSubmit={handleSubmit} noValidate>
        <h3 id="cust-title">{editing ? 'Edit customer' : 'New customer'}</h3>

        <label>
          Name
          <input ref={firstRef} value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} />
          {errors.name && <span className="field-error">{errors.name}</span>}
        </label>

        <label>
          Email (optional)
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} aria-invalid={!!errors.email} />
          {errors.email && <span className="field-error">{errors.email}</span>}
        </label>

        <label>
          Phone (optional)
          <input type="tel" value={phone} onChange={(e) => setPhone(e.target.value)} />
        </label>

        {submitError && <p className="error banner" role="alert">{submitError}</p>}

        <div className="dialog-actions">
          <button type="button" className="btn-ghost" onClick={onCancel} disabled={saving}>Cancel</button>
          <button type="submit" className="btn-primary btn-sm" disabled={saving}>
            {saving ? <span className="spinner" /> : editing ? 'Save changes' : 'Add customer'}
          </button>
        </div>
      </form>
    </div>
  )
}

export default function CustomersPage({ active, user }) {
  const [customers, setCustomers] = useState(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [query, setQuery] = useState('')
  const [form, setForm] = useState(null) // null | { customer?: {...} }
  const [notice, setNotice] = useState('')
  const [deletingId, setDeletingId] = useState(null)

  // Only Admins and Finance Managers can delete customers.
  const canDelete = !!user?.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')

  async function load() {
    setLoading(true)
    setError('')
    try {
      setCustomers(await getCustomers())
    } catch (err) {
      setError(err.message)
    } finally {
      setLoading(false)
    }
  }

  // Fetch fresh data whenever the page becomes active.
  useEffect(() => {
    if (active) load()
  }, [active])

  function handleSaved(saved, kind) {
    setForm(null)
    setNotice(kind === 'created' ? 'Customer added.' : 'Customer updated.')
    load()
  }

  async function handleDelete(c) {
    if (!window.confirm(`Permanently delete "${c.name}"? This cannot be undone.`)) return
    setNotice('')
    setError('')
    setDeletingId(c.id)
    try {
      await deleteCustomer(c.id)
      setNotice(`Customer "${c.name}" was deleted.`)
      await load()
    } catch (err) {
      if (err.status === 400 || err.status === 409) {
        setError(`"${c.name}" cannot be deleted because they have invoices. [${err.message}]`)
      } else if (err.status === 403) {
        setError('Your role does not have permission for this action.')
      } else {
        setError(err.message)
      }
    } finally {
      setDeletingId(null)
    }
  }

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (customers || []).filter(
      (c) =>
        !q ||
        c.name.toLowerCase().includes(q) ||
        (c.email || '').toLowerCase().includes(q) ||
        (c.phone || '').toLowerCase().includes(q),
    )
  }, [customers, query])

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Customers</h2>
        <div className="head-actions">
          <button className="btn-ghost btn-sm" onClick={load} disabled={loading}>Refresh</button>
          <button className="btn-primary btn-sm" onClick={() => { setNotice(''); setForm({}) }}>New customer</button>
        </div>
      </header>

      <div className="page-body">
        {customers && (
          <div className="stat-row">
            <div className="card stat"><span className="muted">Total customers</span><strong>{customers.length}</strong></div>
          </div>
        )}

        <div className="toolbar">
          <input type="search" placeholder="Search by name, email or phone" value={query}
            onChange={(e) => setQuery(e.target.value)} aria-label="Search customers" />
        </div>

        {notice && <p className="notice" role="status">{notice}</p>}
        {error && (
          <p className="error banner" role="alert">
            {error} <button className="link-btn" onClick={load}>Try again</button>
          </p>
        )}

        {loading && !customers && <div className="center-pad"><span className="spinner" style={{ width: 26, height: 26 }} /></div>}

        {customers && filtered.length === 0 && (
          <div className="center-pad muted">
            {customers.length === 0 ? 'No customers yet. Add the first one.' : 'No customers match your search.'}
          </div>
        )}

        {filtered.length > 0 && (
          <div className="card table-wrap">
            <table>
              <thead>
                <tr><th>Name</th><th>Email</th><th>Phone</th><th>Added</th><th /></tr>
              </thead>
              <tbody>
                {filtered.map((c) => (
                  <tr key={c.id}>
                    <td><strong>{c.name}</strong></td>
                    <td>{c.email || '—'}</td>
                    <td>{c.phone || '—'}</td>
                    <td>{dateOnly(c.createdAtUtc)}</td>
                    <td className="actions">
                      <button className="btn-ghost btn-sm" onClick={() => { setNotice(''); setForm({ customer: c }) }}>Edit</button>
                      {canDelete && (
                        <button className="btn-ghost btn-sm" disabled={deletingId === c.id} onClick={() => handleDelete(c)}>
                          {deletingId === c.id ? 'Deleting...' : 'Delete'}
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {form && <CustomerForm customer={form.customer} onCancel={() => setForm(null)} onSaved={handleSaved} />}
    </main>
  )
}
