import { useCallback, useEffect, useState } from "react";
import {
  mgrApprovals,
  mgrInvoices,
  mgrInvoiceAction,
  mgrDeleteInvoice,
  mgrExpenseAction,
  mgrPaymentsFor,
  mgrAddPayment,
  mgrDeletePayment,
  mgrAttempts,
  mgrResolveAttempt,
  mgrRefundAttempt,
  mgrReportSummary,
  mgrReportOverdue,
  mgrSendReminder,
} from "../api";
import "../manager.css";

/* ---------- helpers ---------- */

const TABS = [
  ["approvals", "Approvals"],
  ["invoices", "Invoices"],
  ["payments", "Payments"],
  ["online", "Online payments"],
  ["reports", "Reports"],
];

// Every call has a time limit so the page can never stay in "loading" forever.
function withTimeout(promise, ms = 25000) {
  let timer;
  const limit = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("The server did not respond in time. Is the backend running?")), ms);
  });
  return Promise.race([promise, limit]).finally(() => clearTimeout(timer));
}

function money(amount, currency) {
  const n = Number(amount ?? 0);
  try {
    return new Intl.NumberFormat(undefined, { style: "currency", currency: currency || "PKR" }).format(n);
  } catch {
    return `${currency || ""} ${n.toFixed(2)}`;
  }
}

const day = (v) => (v ? String(v).slice(0, 10) : "-");
const todayLocal = () => new Date().toLocaleDateString("en-CA");

// The shared api.js shows one generic text for every 400. These hints explain
// the real reason on this page.
const HINTS = {
  review: {
    400: "Segregation of duties: the person who created, submitted or edited a record cannot approve or reject it. Please use another manager or an Admin account.",
    409: "This record was changed by someone else. Refresh and try again.",
  },
  payment: {
    400: "A payment can only be recorded on an Approved or Partial invoice, it cannot exceed the outstanding balance, and the invoice must have no online payment under review.",
    409: "This payment was just recorded, or the invoice changed at the same time. Refresh and try again.",
  },
  invoice: {
    400: "This action is not allowed in the invoice's current status (delete: Draft only, cancel: no payments recorded, reopen: Rejected only).",
    403: "Your role does not have permission for this action.",
  },
  reminder: {
    400: "A reminder can only be sent for an Approved or Partial invoice with an outstanding balance, a customer email address, and no reminder in the last 24 hours.",
    502: "The email server (SMTP) could not be reached. Is smtp4dev running?",
  },
  online: {
    400: "This action is not allowed for the attempt's current status.",
    409: "Something changed at the same time. Refresh and try again.",
  },
};

function explain(err, kind) {
  const msg = (err && err.message) || "Something went wrong.";
  const hint = HINTS[kind] && HINTS[kind][err && err.status];
  return hint ? `${hint} [${msg}]` : msg;
}

function useData(loader, enabled) {
  const [state, setState] = useState({ data: null, loading: false, error: "" });
  const reload = useCallback(async () => {
    setState((s) => ({ ...s, loading: true, error: "" }));
    try {
      const data = await withTimeout(loader());
      setState({ data, loading: false, error: "" });
    } catch (e) {
      setState((s) => ({ data: s.data, loading: false, error: explain(e, "load") }));
    }
  }, [loader]);
  useEffect(() => {
    if (enabled) reload();
  }, [enabled, reload]);
  return { ...state, reload };
}

function Badge({ status }) {
  return <span className={`mgr-badge mgr-s-${String(status || "").toLowerCase()}`}>{status}</span>;
}

function Toolbar({ title, loading, onReload, children }) {
  return (
    <div className="mgr-toolbar">
      <h2>{title}</h2>
      <div className="mgr-toolbar-right">
        {children}
        <button type="button" className="mgr-btn" onClick={onReload} disabled={loading}>
          {loading ? "Loading..." : "Refresh"}
        </button>
      </div>
    </div>
  );
}

function LoadState({ loading, error, empty, emptyText }) {
  if (error) return <div className="mgr-note err" role="alert">{error}</div>;
  if (loading && empty) return <div className="mgr-note">Loading...</div>;
  if (!loading && empty) return <div className="mgr-note">{emptyText}</div>;
  return null;
}

/* ---------- 1. Approvals ---------- */

function ApprovalsTab({ enabled, run, busy }) {
  const { data, loading, error, reload } = useData(mgrApprovals, enabled);
  const [comments, setComments] = useState({});
  const setComment = (key, value) => setComments((c) => ({ ...c, [key]: value }));

  const invoices = (data && data.invoices) || [];
  const expenses = (data && data.expenses) || [];

  const actInvoice = (id, action) =>
    run(`inv-${id}-${action}`, () => mgrInvoiceAction(id, action, comments[`inv-${id}`]),
      action === "approve" ? "Invoice approved." : "Invoice rejected.", "review", reload);
  const actExpense = (id, action) =>
    run(`exp-${id}-${action}`, () => mgrExpenseAction(id, action, comments[`exp-${id}`]),
      action === "approve" ? "Expense approved." : "Expense rejected.", "review", reload);

  return (
    <section>
      <Toolbar title="Awaiting your decision" loading={loading} onReload={reload} />
      <p className="mgr-help">
        Only items that you did not create, submit or edit appear here (segregation of duties).
      </p>
      <LoadState loading={loading} error={error} empty={!data} emptyText="Nothing to show." />

      {data && (
        <>
          <h3>Invoices ({invoices.length})</h3>
          {invoices.length === 0 ? (
            <div className="mgr-note">No invoices are awaiting approval.</div>
          ) : (
            <div className="mgr-table-wrap">
              <table className="mgr-table">
                <thead>
                  <tr><th>Invoice</th><th>Customer</th><th>Total</th><th>Due</th><th>Comment</th><th></th></tr>
                </thead>
                <tbody>
                  {invoices.map((i) => (
                    <tr key={i.id}>
                      <td>{i.invoiceNumber}</td>
                      <td>{i.customer || "-"}</td>
                      <td>{money(i.total, i.currency)}</td>
                      <td>{day(i.dueDate)}</td>
                      <td>
                        <input className="mgr-input" placeholder="Optional" maxLength={500}
                          value={comments[`inv-${i.id}`] || ""} onChange={(e) => setComment(`inv-${i.id}`, e.target.value)} />
                      </td>
                      <td className="mgr-actions">
                        <button type="button" className="mgr-btn ok" disabled={!!busy} onClick={() => actInvoice(i.id, "approve")}>Approve</button>
                        <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => actInvoice(i.id, "reject")}>Reject</button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          <h3>Expenses ({expenses.length})</h3>
          {expenses.length === 0 ? (
            <div className="mgr-note">No expenses are awaiting approval.</div>
          ) : (
            <div className="mgr-table-wrap">
              <table className="mgr-table">
                <thead>
                  <tr><th>Title</th><th>Category</th><th>Amount</th><th>Date</th><th>Comment</th><th></th></tr>
                </thead>
                <tbody>
                  {expenses.map((x) => (
                    <tr key={x.id}>
                      <td>{x.title}</td>
                      <td>{x.category}</td>
                      <td>{money(x.amount, x.currency)}</td>
                      <td>{day(x.expenseDate)}</td>
                      <td>
                        <input className="mgr-input" placeholder="Optional" maxLength={500}
                          value={comments[`exp-${x.id}`] || ""} onChange={(e) => setComment(`exp-${x.id}`, e.target.value)} />
                      </td>
                      <td className="mgr-actions">
                        <button type="button" className="mgr-btn ok" disabled={!!busy} onClick={() => actExpense(x.id, "approve")}>Approve</button>
                        <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => actExpense(x.id, "reject")}>Reject</button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}

/* ---------- 2. Invoices (cancel, delete, reopen, reminder) ---------- */

const STATUSES = ["All", "Draft", "PendingApproval", "Approved", "Rejected", "Partial", "Paid", "Cancelled"];

function InvoicesTab({ enabled, run, busy }) {
  const { data, loading, error, reload } = useData(mgrInvoices, enabled);
  const [status, setStatus] = useState("All");
  const [q, setQ] = useState("");

  const list = (data || []).filter((i) => {
    if (status !== "All" && i.status !== status) return false;
    const text = `${i.invoiceNumber} ${(i.customer && i.customer.name) || ""}`.toLowerCase();
    return text.includes(q.trim().toLowerCase());
  });

  const ask = (text) => window.confirm(text);

  const reopen = (i) =>
    run(`i-${i.id}-reopen`, () => mgrInvoiceAction(i.id, "reopen"), `${i.invoiceNumber} was reopened as a Draft.`, "invoice", reload);
  const cancel = (i) =>
    ask(`Cancel ${i.invoiceNumber}? This cannot be undone.`) &&
    run(`i-${i.id}-cancel`, () => mgrInvoiceAction(i.id, "cancel"), `${i.invoiceNumber} was cancelled.`, "invoice", reload);
  const remove = (i) =>
    ask(`Permanently delete draft ${i.invoiceNumber}?`) &&
    run(`i-${i.id}-delete`, () => mgrDeleteInvoice(i.id), `${i.invoiceNumber} was deleted.`, "invoice", reload);
  const remind = (i) =>
    run(`i-${i.id}-remind`, () => mgrSendReminder(i.id), `Reminder email sent for ${i.invoiceNumber}.`, "reminder", reload);

  return (
    <section>
      <Toolbar title="All invoices" loading={loading} onReload={reload}>
        <input className="mgr-input" placeholder="Search by number or customer" value={q} onChange={(e) => setQ(e.target.value)} />
        <select className="mgr-input" value={status} onChange={(e) => setStatus(e.target.value)}>
          {STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
        </select>
      </Toolbar>
      <LoadState loading={loading} error={error} empty={!data || list.length === 0} emptyText="No invoices found." />
      {list.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead>
              <tr><th>Invoice</th><th>Customer</th><th>Total</th><th>Due</th><th>Status</th><th>Actions</th></tr>
            </thead>
            <tbody>
              {list.map((i) => (
                <tr key={i.id}>
                  <td>{i.invoiceNumber}{i.isExcluded && <span className="mgr-tag">excluded</span>}</td>
                  <td>{(i.customer && i.customer.name) || "-"}</td>
                  <td>{money(i.total, i.currency)}</td>
                  <td>{day(i.dueDate)}</td>
                  <td><Badge status={i.status} /></td>
                  <td className="mgr-actions">
                    {i.status === "Rejected" && <button type="button" className="mgr-btn" disabled={!!busy} onClick={() => reopen(i)}>Reopen</button>}
                    {(i.status === "Approved" || i.status === "Partial") && !i.isExcluded &&
                      <button type="button" className="mgr-btn" disabled={!!busy} onClick={() => remind(i)}>Send reminder</button>}
                    {["Draft", "PendingApproval", "Approved", "Rejected"].includes(i.status) &&
                      <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => cancel(i)}>Cancel</button>}
                    {i.status === "Draft" &&
                      <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => remove(i)}>Delete</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ---------- 3. Manual payments ---------- */

function PaymentsTab({ enabled, run, busy }) {
  const invoices = useData(mgrInvoices, enabled);
  const [selected, setSelected] = useState("");
  const loadDetail = useCallback(
    () => (selected ? mgrPaymentsFor(selected) : Promise.resolve(null)),
    [selected]
  );
  const detail = useData(loadDetail, enabled && !!selected);

  const [amount, setAmount] = useState("");
  const [date, setDate] = useState(todayLocal());
  const [method, setMethod] = useState("Cash");
  const [note, setNote] = useState("");

  const payable = (invoices.data || []).filter((i) => (i.status === "Approved" || i.status === "Partial") && !i.isExcluded);
  const d = detail.data;

  const refreshAll = async () => {
    await detail.reload();
    await invoices.reload();
  };

  const submit = (e) => {
    e.preventDefault();
    const value = Number(amount);
    if (!selected) return run("p-none", () => Promise.reject(new Error("Please select an invoice first.")), "", "payment");
    if (!(value > 0)) return run("p-none", () => Promise.reject(new Error("The amount must be greater than zero.")), "", "payment");
    return run(
      "p-add",
      () => mgrAddPayment({ invoiceId: Number(selected), amount: value, paymentDate: date, method: method || "Cash", note: note.trim() || undefined }),
      "Payment recorded. The invoice status was updated automatically.",
      "payment",
      async () => { setAmount(""); setNote(""); await refreshAll(); }
    );
  };

  const removePayment = (p) =>
    window.confirm("Delete this payment?") &&
    run(`p-${p.id}`, () => mgrDeletePayment(p.id), "Payment deleted.", "payment", refreshAll);

  return (
    <section>
      <Toolbar title="Record a manual payment" loading={invoices.loading || detail.loading} onReload={refreshAll} />
      <LoadState loading={invoices.loading} error={invoices.error} empty={false} />

      <div className="mgr-form">
        <label>
          Invoice
          <select className="mgr-input" value={selected} onChange={(e) => setSelected(e.target.value)}>
            <option value="">Select an Approved or Partial invoice</option>
            {payable.map((i) => (
              <option key={i.id} value={i.id}>
                {i.invoiceNumber} - {(i.customer && i.customer.name) || "no customer"} - {money(i.total, i.currency)} ({i.status})
              </option>
            ))}
          </select>
        </label>
        {!invoices.loading && payable.length === 0 && <div className="mgr-note">There are no Approved or Partial invoices right now.</div>}
      </div>

      {selected && detail.error && <div className="mgr-note err" role="alert">{detail.error}</div>}

      {d && (
        <>
          <div className="mgr-cards">
            <div className="mgr-card"><span>Invoice total</span><strong>{money(d.invoiceAmount, d.currency)}</strong></div>
            <div className="mgr-card"><span>Paid so far</span><strong>{money(d.totalPaid, d.currency)}</strong></div>
            <div className="mgr-card"><span>Balance</span><strong>{money(d.balance, d.currency)}</strong></div>
            <div className="mgr-card"><span>Status</span><strong><Badge status={d.status} /></strong></div>
          </div>

          <form className="mgr-form row" onSubmit={submit}>
            <label>Amount ({d.currency})
              <input className="mgr-input" type="number" step="0.01" min="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} placeholder={String(d.balance ?? "")} />
            </label>
            <label>Date
              <input className="mgr-input" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
            </label>
            <label>Method
              <select className="mgr-input" value={method} onChange={(e) => setMethod(e.target.value)}>
                <option>Cash</option><option>Bank Transfer</option><option>Cheque</option><option>Other</option>
              </select>
            </label>
            <label>Note
              <input className="mgr-input" maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} placeholder="Optional" />
            </label>
            <button type="submit" className="mgr-btn ok" disabled={!!busy}>Add payment</button>
          </form>

          <h3>Payment history</h3>
          {d.payments && d.payments.length > 0 ? (
            <div className="mgr-table-wrap">
              <table className="mgr-table">
                <thead><tr><th>Date</th><th>Amount</th><th>Method</th><th>Note</th><th></th></tr></thead>
                <tbody>
                  {d.payments.map((p) => (
                    <tr key={p.id}>
                      <td>{day(p.paymentDate)}</td>
                      <td>{money(p.amount, d.currency)}</td>
                      <td>{p.method}</td>
                      <td>{p.note || "-"}</td>
                      <td className="mgr-actions">
                        {p.paymentAttemptId == null && p.amount > 0 &&
                          <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => removePayment(p)}>Delete</button>}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <div className="mgr-note">No payments have been recorded for this invoice yet.</div>
          )}
        </>
      )}
    </section>
  );
}

/* ---------- 4. Online (Stripe) payments ---------- */

const ATTEMPT_STATUSES = ["", "NeedsReview", "Pending", "Succeeded", "Failed", "Expired", "Refunded", "Resolved"];

function OnlineTab({ enabled, run, busy, isAdmin }) {
  const [status, setStatus] = useState("");
  const loader = useCallback(() => mgrAttempts(status), [status]);
  const { data, loading, error, reload } = useData(loader, enabled);

  const resolve = (a, action) => {
    const note = window.prompt(
      action === "apply" ? "Optional note: why is this payment being applied?" : "Optional note: why is this attempt being dismissed?",
      ""
    );
    if (note === null) return null;
    return run(`a-${a.id}-${action}`, () => mgrResolveAttempt(a.id, action, note),
      action === "apply" ? "The payment was applied to the invoice." : "The attempt was dismissed.", "online", reload);
  };
  const refund = (a) => {
    if (!window.confirm(`Refund ${a.currency} ${a.amount} on Stripe? This cannot be undone.`)) return null;
    return run(`a-${a.id}-refund`, () => mgrRefundAttempt(a.id), "Refund completed.", "online", reload);
  };

  const list = data || [];
  return (
    <section>
      <Toolbar title="Stripe attempts" loading={loading} onReload={reload}>
        <select className="mgr-input" value={status} onChange={(e) => setStatus(e.target.value)}>
          {ATTEMPT_STATUSES.map((s) => <option key={s || "all"} value={s}>{s || "All statuses"}</option>)}
        </select>
      </Toolbar>
      <p className="mgr-help">
        An invoice becomes Paid only when Stripe's signed webhook reaches the server. NeedsReview means the money was
        charged but could not be matched automatically, so a person must decide how to handle it.
      </p>
      <LoadState loading={loading} error={error} empty={list.length === 0} emptyText="No online payment attempts." />
      {list.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>Invoice</th><th>Amount</th><th>Status</th><th>Created</th><th>Details</th><th>Actions</th></tr></thead>
            <tbody>
              {list.map((a) => (
                <tr key={a.id}>
                  <td>{a.invoiceNumber || `#${a.invoiceId}`}</td>
                  <td>{money(a.amount, a.currency)}</td>
                  <td><Badge status={a.status} /></td>
                  <td>{day(a.createdAtUtc)}</td>
                  <td className="mgr-small">{a.failureReason || a.resolutionNote || (a.refundedAmount > 0 ? `Refunded ${a.refundedAmount}` : "-")}</td>
                  <td className="mgr-actions">
                    {a.status === "NeedsReview" && (
                      <>
                        <button type="button" className="mgr-btn ok" disabled={!!busy} onClick={() => resolve(a, "apply")}>Apply</button>
                        <button type="button" className="mgr-btn" disabled={!!busy} onClick={() => resolve(a, "dismiss")}>Dismiss</button>
                      </>
                    )}
                    {isAdmin && (a.status === "Succeeded" || a.status === "NeedsReview") &&
                      <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => refund(a)}>Refund</button>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ---------- 5. Reports ---------- */

function ReportsTab({ enabled, run, busy }) {
  const summary = useData(mgrReportSummary, enabled);
  const overdue = useData(mgrReportOverdue, enabled);
  const reloadAll = async () => { await summary.reload(); await overdue.reload(); };

  const remind = (o) =>
    run(`o-${o.id}`, () => mgrSendReminder(o.id), `Reminder sent for ${o.invoiceNumber}.`, "reminder", overdue.reload);

  const lines = (summary.data && summary.data.currencies) || [];
  const late = overdue.data || [];
  return (
    <section>
      <Toolbar title="Summary by currency" loading={summary.loading || overdue.loading} onReload={reloadAll} />
      <p className="mgr-help">Only Approved, Partial and Paid invoices are counted. Different currencies are never added together.</p>
      <LoadState loading={summary.loading} error={summary.error} empty={lines.length === 0} emptyText="There are no reportable invoices yet." />
      {lines.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>Currency</th><th>Invoices</th><th>Invoiced</th><th>Paid</th><th>Receivable</th><th>Overdue</th></tr></thead>
            <tbody>
              {lines.map((c) => (
                <tr key={c.currency}>
                  <td>{c.currency}</td>
                  <td>{c.invoiceCount}</td>
                  <td>{money(c.totalInvoiced, c.currency)}</td>
                  <td>{money(c.totalPaid, c.currency)}</td>
                  <td>{money(c.totalReceivable, c.currency)}</td>
                  <td>{c.overdueCount} ({money(c.overdueAmount, c.currency)})</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <h3>Overdue invoices ({late.length})</h3>
      <LoadState loading={overdue.loading} error={overdue.error} empty={late.length === 0} emptyText="There are no overdue invoices." />
      {late.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>Invoice</th><th>Customer</th><th>Due</th><th>Days overdue</th><th>Balance</th><th></th></tr></thead>
            <tbody>
              {late.map((o) => (
                <tr key={o.id}>
                  <td>{o.invoiceNumber}</td>
                  <td>{o.customer || "-"}</td>
                  <td>{day(o.dueDate)}</td>
                  <td>{o.daysOverdue}</td>
                  <td>{money(o.balance, o.currency)}</td>
                  <td className="mgr-actions">
                    <button type="button" className="mgr-btn" disabled={!!busy} onClick={() => remind(o)}>Send reminder</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ---------- the page ---------- */

export default function ManagerPage({ active, user }) {
  const roles = (user && user.roles) || [];
  const isAdmin = roles.includes("Admin");
  const [tab, setTab] = useState("approvals");
  const [busy, setBusy] = useState("");
  const [notice, setNotice] = useState(null);

  // Runs one action, shows a success or error message, then refreshes the list.
  const run = useCallback(async (key, fn, okText, kind, after) => {
    setBusy(key);
    setNotice(null);
    try {
      await withTimeout(fn());
      if (okText) setNotice({ type: "ok", text: okText });
      if (after) await after();
    } catch (e) {
      setNotice({ type: "err", text: explain(e, kind) });
    } finally {
      setBusy("");
    }
  }, []);

  const common = { run, busy };

  return (
    <div className="mgr">
      <div className="mgr-head">
        <h1>Manager tools</h1>
        <p>Approvals, invoice actions, payments, Stripe review and reports. Every action is recorded in the audit log.</p>
      </div>

      <div className="mgr-tabs" role="tablist">
        {TABS.map(([id, label]) => (
          <button key={id} type="button" role="tab" aria-selected={tab === id}
            className={`mgr-tab${tab === id ? " on" : ""}`} onClick={() => { setTab(id); setNotice(null); }}>
            {label}
          </button>
        ))}
      </div>

      {notice && (
        <div className={`mgr-note ${notice.type}`} role={notice.type === "err" ? "alert" : "status"}>
          {notice.text}
        </div>
      )}

      {tab === "approvals" && <ApprovalsTab enabled={active} {...common} />}
      {tab === "invoices" && <InvoicesTab enabled={active} {...common} />}
      {tab === "payments" && <PaymentsTab enabled={active} {...common} />}
      {tab === "online" && <OnlineTab enabled={active} isAdmin={isAdmin} {...common} />}
      {tab === "reports" && <ReportsTab enabled={active} {...common} />}
    </div>
  );
}
