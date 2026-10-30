import { useEffect, useRef, useState } from 'react'
import { getCustomers, createInvoice } from '../api'

const CURRENCIES = ['PKR', 'USD', 'EUR', 'GBP', 'AED']

// Local date (UTC nahi), taake subah ke waqt tareekh ek din peeche na jaye
const fmt = (d) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
const plusDays = (n) => {
  const d = new Date()
  d.setDate(d.getDate() + n)
  return fmt(d)
}

export default function InvoiceForm({ onCancel, onCreated }) {
  const [customers, setCustomers] = useState(null)
  const [loadError, setLoadError] = useState('')

  const [invoiceNumber, setInvoiceNumber] = useState('')
  const [customerId, setCustomerId] = useState('')
  const [description, setDescription] = useState('')
  const [invoiceDate, setInvoiceDate] = useState(plusDays(0))
  const [dueDate, setDueDate] = useState(plusDays(30))
  const [currency, setCurrency] = useState('PKR')
  const [subtotal, setSubtotal] = useState('')
  const [taxAmount, setTaxAmount] = useState('')

  const [errors, setErrors] = useState({})
  const [saving, setSaving] = useState(false)
  const [submitError, setSubmitError] = useState('')
  const firstRef = useRef(null)

  useEffect(() => {
    let cancelled = false
    getCustomers()
      .then((list) => !cancelled && setCustomers(list))
      .catch((err) => !cancelled && setLoadError(err.message))
    return () => {
      cancelled = true
    }
  }, [])

  useEffect(() => {
    firstRef.current?.focus()
    const onKey = (e) => e.key === 'Escape' && !saving && onCancel()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [saving, onCancel])

  function validate() {
    const v = {}
    if (!invoiceNumber.trim()) v.invoiceNumber = 'Invoice number is required.'
    else if (invoiceNumber.trim().length > 50) v.invoiceNumber = 'Use 50 characters or fewer.'
    if (!customerId) v.customerId = 'Choose a customer.'
    if (!(Number(subtotal) >= 0.01)) v.subtotal = 'Enter an amount of at least 0.01.'
    if (taxAmount !== '' && !(Number(taxAmount) >= 0)) v.taxAmount = 'Tax cannot be negative.'
    if (!invoiceDate) v.invoiceDate = 'Choose a date.'
    if (!dueDate) v.dueDate = 'Choose a due date.'
    else if (invoiceDate && dueDate < invoiceDate) v.dueDate = 'Due date cannot be before the invoice date.'
    if (description.length > 500) v.description = 'Use 500 characters or fewer.'
    return v
  }

  async function handleSubmit(e) {
    e.preventDefault()
    const v = validate()
    setErrors(v)
    if (Object.keys(v).length) return

    setSaving(true)
    setSubmitError('')
    try {
      await createInvoice({
        invoiceNumber: invoiceNumber.trim(),
        customerId: Number(customerId),
        description: description.trim() || null,
        invoiceDate,
        dueDate,
        currency,
        subtotal: Number(subtotal),
        taxAmount: Number(taxAmount) || 0,
        isExcluded: false,
      })
      onCreated()
    } catch (err) {
      setSubmitError(
        err.status === 400
          ? 'Could not save. This invoice number may already exist, or a value is invalid.'
          : err.message,
      )
      setSaving(false)
    }
  }

  // Sirf dikhane ke liye. Asli total backend nikalta hai.
  const totalPreview = (Number(subtotal) || 0) + (Number(taxAmount) || 0)
  let totalText = totalPreview.toFixed(2)
  try {
    totalText = new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(totalPreview)
  } catch {
    /* default text */
  }

  const noCustomers = customers && customers.length === 0

  return (
    <div className="overlay" onClick={() => !saving && onCancel()}>
      <form className="card dialog wide" role="dialog" aria-modal="true" aria-labelledby="inv-title"
        onClick={(e) => e.stopPropagation()} onSubmit={handleSubmit} noValidate>
        <h3 id="inv-title">New invoice</h3>
        <p className="muted">It is saved as a draft. You can submit it for approval afterwards.</p>

        {!customers && !loadError && <div className="center-pad"><span className="spinner" /></div>}
        {loadError && <p className="error banner" role="alert">{loadError}</p>}
        {noCustomers && (
          <p className="error banner" role="alert">
            There are no customers yet. Create a customer first, then come back.
          </p>
        )}

        {customers && customers.length > 0 && (
          <>
            <div className="form-grid">
              <label>
                Invoice number
                <input ref={firstRef} value={invoiceNumber} onChange={(e) => setInvoiceNumber(e.target.value)}
                  placeholder="INV-1001" aria-invalid={!!errors.invoiceNumber} />
                {errors.invoiceNumber && <span className="field-error">{errors.invoiceNumber}</span>}
              </label>

              <label>
                Customer
                <select value={customerId} onChange={(e) => setCustomerId(e.target.value)} aria-invalid={!!errors.customerId}>
                  <option value="">Choose a customer</option>
                  {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
                {errors.customerId && <span className="field-error">{errors.customerId}</span>}
              </label>

              <label>
                Invoice date
                <input type="date" value={invoiceDate} onChange={(e) => setInvoiceDate(e.target.value)}
                  aria-invalid={!!errors.invoiceDate} />
                {errors.invoiceDate && <span className="field-error">{errors.invoiceDate}</span>}
              </label>

              <label>
                Due date
                <input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)}
                  aria-invalid={!!errors.dueDate} />
                {errors.dueDate && <span className="field-error">{errors.dueDate}</span>}
              </label>

              <label>
                Currency
                <select value={currency} onChange={(e) => setCurrency(e.target.value)}>
                  {CURRENCIES.map((c) => <option key={c} value={c}>{c}</option>)}
                </select>
              </label>

              <label>
                Subtotal
                <input type="number" inputMode="decimal" min="0" step="0.01" value={subtotal}
                  onChange={(e) => setSubtotal(e.target.value)} placeholder="0.00" aria-invalid={!!errors.subtotal} />
                {errors.subtotal && <span className="field-error">{errors.subtotal}</span>}
              </label>

              <label>
                Tax amount
                <input type="number" inputMode="decimal" min="0" step="0.01" value={taxAmount}
                  onChange={(e) => setTaxAmount(e.target.value)} placeholder="0.00" aria-invalid={!!errors.taxAmount} />
                {errors.taxAmount && <span className="field-error">{errors.taxAmount}</span>}
              </label>

              <div className="total-preview">
                <span className="muted">Total (calculated by the server)</span>
                <strong>{totalText}</strong>
              </div>
            </div>

            <label>
              Description (optional)
              <textarea rows={2} value={description} onChange={(e) => setDescription(e.target.value)}
                aria-invalid={!!errors.description} />
              {errors.description && <span className="field-error">{errors.description}</span>}
            </label>
          </>
        )}

        {submitError && <p className="error banner" role="alert">{submitError}</p>}

        <div className="dialog-actions">
          <button type="button" className="btn-ghost" onClick={onCancel} disabled={saving}>Cancel</button>
          <button type="submit" className="btn-primary btn-sm" disabled={saving || !customers || noCustomers}>
            {saving ? <span className="spinner" /> : 'Create draft'}
          </button>
        </div>
      </form>
    </div>
  )
}