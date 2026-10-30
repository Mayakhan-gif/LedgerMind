import { useEffect, useState } from 'react'
import { getXeroStatus, getXeroInvoices, getXeroConnectUrl } from '../api'
import { money, dateOnly } from '../format'

const TYPE_LABEL = { ACCREC: 'Sales invoice', ACCPAY: 'Bill' }

export default function IntegrationsPage({ active, user, onNavigate }) {
  const isAdmin = user.roles?.includes('Admin')

  const [status, setStatus] = useState(null)
  const [statusError, setStatusError] = useState('')
  const [xero, setXero] = useState(null)
  const [dataError, setDataError] = useState('')
  const [loading, setLoading] = useState(false)
  const [connecting, setConnecting] = useState(false)
  const [connectError, setConnectError] = useState('')
  const [fallbackUrl, setFallbackUrl] = useState('')
  const [notice, setNotice] = useState('')

  async function load() {
    setLoading(true)
    // Ek fail ho to dusra phir bhi dikhe
    const [s, d] = await Promise.allSettled([getXeroStatus(), getXeroInvoices()])
    setStatus(s.status === 'fulfilled' ? s.value : null)
    setStatusError(s.status === 'rejected' ? s.reason.message : '')
    setXero(d.status === 'fulfilled' ? d.value : null)
    setDataError(d.status === 'rejected' ? d.reason.message : '')
    setLoading(false)
  }

  useEffect(() => {
    if (active) load()
  }, [active])

  async function connect() {
    setConnecting(true)
    setConnectError('')
    setFallbackUrl('')
    setNotice('')
    try {
      const url = await getXeroConnectUrl()
      const win = window.open(url, '_blank')
      if (win) win.opener = null
      else setFallbackUrl(url) // popup block ho gaya
      setNotice(
        'Finish signing in to Xero in the new tab, then come back here and press "Refresh". The link expires after 10 minutes.',
      )
    } catch (err) {
      setConnectError(err.message)
    } finally {
      setConnecting(false)
    }
  }

  const invoices = xero?.invoices || []

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Integrations</h2>
        <div className="head-actions">
          <button className="btn-ghost btn-sm" onClick={load} disabled={loading}>Refresh</button>
        </div>
      </header>

      <div className="page-body">
        <div className="int-grid">
          <section className="card int-card">
            <h3>Xero (accounting)</h3>

            {loading && !status && !statusError ? (
              <span className="spinner" />
            ) : statusError ? (
              <p className="error" role="alert" style={{ textAlign: 'left' }}>{statusError}</p>
            ) : status?.connected ? (
              <div className="int-status">
                <span className="dot on" />
                <span>
                  Connected to <strong>{status.tenantName || 'Xero'}</strong>
                  {status.connectedAtUtc && <span className="muted"> since {dateOnly(status.connectedAtUtc)}</span>}
                </span>
              </div>
            ) : (
              <div className="int-status"><span className="dot" /><span>Not connected</span></div>
            )}

            <p className="muted">
              Read-only access to invoices and contacts. You sign in on Xero’s own page, so this app never sees your Xero password.
            </p>

            {isAdmin ? (
              <div className="int-actions">
                <button className="btn-primary btn-sm" onClick={connect} disabled={connecting}>
                  {connecting ? <span className="spinner" /> : status?.connected ? 'Reconnect Xero' : 'Connect Xero'}
                </button>
              </div>
            ) : (
              <p className="muted">Only an Admin can connect Xero.</p>
            )}

            {connectError && <p className="error banner" role="alert">{connectError}</p>}
            {notice && <p className="notice" role="status">{notice}</p>}
            {fallbackUrl && (
              <p className="muted">
                Your browser blocked the new tab.{' '}
                <a className="link-btn" href={fallbackUrl} target="_blank" rel="noreferrer">Open Xero sign-in</a>
              </p>
            )}
          </section>

          <section className="card int-card">
            <h3>Stripe (payments)</h3>
            <div className="int-status"><span className="dot" /><span>Test mode</span></div>
            <p className="muted">
              Pay an approved invoice with the “Pay online” button on the Invoices page. Card details are entered on Stripe’s page and never reach this app. An invoice becomes Paid only after Stripe’s signed confirmation reaches the server.
            </p>
            <div className="int-actions">
              <button className="btn-ghost btn-sm" onClick={() => onNavigate('invoices')}>Go to invoices</button>
            </div>
          </section>
        </div>

        <h3 style={{ marginTop: 6 }}>Xero invoices</h3>

        {xero?.isDemo && (
          <p className="demo-banner" role="note">
            <strong>DEMO DATA.</strong> {xero.note || 'These are not from a real Xero account.'} Connect Xero (Admin only) to see live invoices from your Xero organisation.
          </p>
        )}

        {dataError && <p className="error banner" role="alert">{dataError}</p>}

        {xero && invoices.length === 0 && <div className="center-pad muted">No invoices to show.</div>}

        {invoices.length > 0 && (
          <div className="card table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Invoice</th><th>Type</th><th>Contact</th><th>Due date</th>
                  <th className="num">Total</th><th className="num">Amount due</th><th>Status</th>
                </tr>
              </thead>
              <tbody>
                {invoices.map((i) => (
                  <tr key={i.invoiceId}>
                    <td><strong>{i.invoiceNumber}</strong></td>
                    <td>{TYPE_LABEL[i.type] || i.type}</td>
                    <td>{i.contactName || '—'}</td>
                    <td>{dateOnly(i.dueDate)}</td>
                    <td className="num">{money(i.total, i.currencyCode)}</td>
                    <td className="num">{money(i.amountDue, i.currencyCode)}</td>
                    <td><span className={`badge s-${(i.status || '').toLowerCase()}`}>{i.status}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </main>
  )
}