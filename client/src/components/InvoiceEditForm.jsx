import { useEffect, useRef, useState } from 'react'
import { getCustomers, updateInvoice } from '../api'

const dayOf = (v) => (v ? String(v).slice(0, 10) : '')

// Edit dialog for a Draft invoice. Uses the same dialog styles as CustomerForm.
export default function InvoiceEditForm({ invoice, onCancel, onSaved }) {
  const [customers, setCustomers] = useState([])
  const [invoiceNumber, setInvoiceNumber] = useState(invoice.invoiceNumber || '')
  const [customerId, setCustomerId] = useState(String(invoice.customerId ?? invoice.customer?.id ?? ''))
  const [description, setDescription] = useState(invoice.description || '')
  const [invoiceDate, setInvoiceDate] = useState(dayOf(invoice.invoiceDate))
  const [dueDate, setDueDate] = useState(dayOf(invoice.dueDate))
  const [currency, setCurrency] = useState(invoice.currency || 'PKR')
  const [subtotal, setSubtotal] = useState(String(invoice.subtotal ?? ''))
  const [taxAmount, setTaxAmount] = useState(String(invoice.taxAmount ?? 0))
  const [isExcluded, setIsExcluded] = useState(!!invoice.isExcluded)
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

  useEffect(() => {
    let alive = true
    getCustomers()
      .then((list) => alive && setCustomers(list || []))
      .catch(() => alive && setSubmitError('Could not load the customer list.'))
    return () => { alive = false }
  }, [])

  const total = (Number(subtotal) || 0) + (Number(taxAmount) || 0)

  function validate() {
    const v = {}
    if (!invoiceNumber.trim()) v.invoiceNumber = 'Invoice number is required.'
    if (!customerId) v.customerId = 'Select a customer.'
    if (!invoiceDate) v.invoiceDate = 'Invoice date is required.'
    if (!dueDate) v.dueDate = 'Due date is required.'
    if (invoiceDate && dueDate && dueDate < invoiceDate) v.dueDate = 'Due date cannot be before the invoice date.'
    if (!/^[A-Za-z]{3}$/.test(currency.trim())) v.currency = 'Use a 3-letter currency code, e.g. PKR.'
    if (!(Number(subtotal) >= 0.01)) v.subtotal = 'Subtotal must be at least 0.01.'
    if (taxAmount !== '' && Number(taxAmount) < 0) v.taxAmount = 'Tax cannot be negative.'
    return v
  }

  async function handleSubmit(e) {
    e.preventDefault()
    const v = validate()
    setErrors(v)
    if (Object.keys(v).length) return

    const payload = {
      invoiceNumber: invoiceNumber.trim(),
      customerId: Number(customerId),
      description: description.trim() || null,
      invoiceDate: `${invoiceDate}T00:00:00Z`,
      dueDate: `${dueDate}T00:00:00Z`,
      currency: currency.trim().toUpperCase(),
      subtotal: Number(subtotal),
      taxAmount: Number(taxAmount) || 0,
      isExcluded,
    }

    setSaving(true)
    setSubmitError('')
    try {
      await updateInvoice(invoice.id, payload)
      onSaved()
    } catch (err) {
      setSubmitError(
        err.status === 400
          ? 'Could not save. Only Draft invoices can be edited, and the invoice number must be unique. Please check the values.'
          : err.message,
      )
      setSaving(false)
    }
  }

  return (
    <div className="overlay" onClick={() => !saving && onCancel()}>
      <form className="card dialog" role="dialog" aria-modal="true" aria-labelledby="inv-edit-title"
        onClick={(e) => e.stopPropagation()} onSubmit={handleSubmit} noValidate>
        <h3 id="inv-edit-title">Edit invoice {invoice.invoiceNumber}</h3>

        <label>
          Invoice number
          <input ref={firstRef} value={invoiceNumber} maxLength={50} onChange={(e) => setInvoiceNumber(e.target.value)} aria-invalid={!!errors.invoiceNumber} />
          {errors.invoiceNumber && <span className="field-error">{errors.invoiceNumber}</span>}
        </label>

        <label>
          Customer
          <select value={customerId} onChange={(e) => setCustomerId(e.target.value)} aria-invalid={!!errors.customerId}>
            <option value="">Select a customer</option>
            {customers.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
          {errors.customerId && <span className="field-error">{errors.customerId}</span>}
        </label>

        <label>
          Description (optional)
          <input value={description} maxLength={500} onChange={(e) => setDescription(e.target.value)} />
        </label>

        <label>
          Invoice date
          <input type="date" value={invoiceDate} onChange={(e) => setInvoiceDate(e.target.value)} aria-invalid={!!errors.invoiceDate} />
          {errors.invoiceDate && <span className="field-error">{errors.invoiceDate}</span>}
        </label>

        <label>
          Due date
          <input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} aria-invalid={!!errors.dueDate} />
          {errors.dueDate && <span className="field-error">{errors.dueDate}</span>}
        </label>

        <label>
          Currency
          <input value={currency} maxLength={3} onChange={(e) => setCurrency(e.target.value.toUpperCase())} aria-invalid={!!errors.currency} />
          {errors.currency && <span className="field-error">{errors.currency}</span>}
        </label>

        <label>
          Subtotal
          <input type="number" step="0.01" min="0.01" value={subtotal} onChange={(e) => setSubtotal(e.target.value)} aria-invalid={!!errors.subtotal} />
          {errors.subtotal && <span className="field-error">{errors.subtotal}</span>}
        </label>

        <label>
          Tax amount
          <input type="number" step="0.01" min="0" value={taxAmount} onChange={(e) => setTaxAmount(e.target.value)} aria-invalid={!!errors.taxAmount} />
          {errors.taxAmount && <span className="field-error">{errors.taxAmount}</span>}
        </label>

        <label style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}>
          <input type="checkbox" checked={isExcluded} onChange={(e) => setIsExcluded(e.target.checked)} />
          Exclude from payments and reminders
        </label>

        <p className="muted">Total: {currency || ''} {total.toFixed(2)}</p>

        {submitError && <p className="error banner" role="alert">{submitError}</p>}

        <div className="dialog-actions">
          <button type="button" className="btn-ghost" onClick={onCancel} disabled={saving}>Cancel</button>
          <button type="submit" className="btn-primary btn-sm" disabled={saving}>
            {saving ? <span className="spinner" /> : 'Save changes'}
          </button>
        </div>
      </form>
    </div>
  )
}
