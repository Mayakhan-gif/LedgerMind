import { useCallback, useEffect, useState } from "react";
import {
  admUsers, admCreateUser, admSetRole, admUnlock,
  admAudit, admEmailLogs, admRunOverdue, admRunCleanup,
} from "../api";
import "../manager.css";

const TABS = [
  ["users", "Users"],
  ["audit", "Audit log"],
  ["email", "Email logs"],
  ["jobs", "Jobs"],
];
const ROLES = ["Employee", "FinanceManager", "Admin"];

function withTimeout(promise, ms = 25000) {
  let timer;
  const limit = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("The server did not respond in time. Is the backend running?")), ms);
  });
  return Promise.race([promise, limit]).finally(() => clearTimeout(timer));
}

const when = (v) => (v ? String(v).replace("T", " ").slice(0, 19) : "-");

const HINTS = {
  role: {
    400: "The role could not be changed. The last Admin cannot be demoted, and the role name must be valid.",
    404: "User not found.",
  },
  create: {
    400: "The user could not be created. The email may already exist, the password must be at least 6 characters, and the role must be valid.",
    409: "This email address already exists.",
  },
  unlock: { 400: "This account is not locked, or it could not be unlocked." },
};

function explain(err, kind) {
  const msg = (err && err.message) || "Something went wrong.";
  if (err && err.status === 403) return "Only an Admin can perform this action.";
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

function Badge({ text }) {
  return <span className={`mgr-badge mgr-s-${String(text || "").toLowerCase()}`}>{text}</span>;
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

/* ---------- Users ---------- */

function normalizeUser(u) {
  const roles = Array.isArray(u.roles) ? u.roles : u.role ? [u.role] : [];
  return {
    id: u.id,
    name: u.fullName || u.name || "-",
    email: u.email || u.userName || "-",
    roles,
    locked: !!(u.isLockedOut ?? u.lockedOut ?? u.isLocked ?? u.locked),
  };
}

function UsersTab({ enabled, run, busy, me }) {
  const { data, loading, error, reload } = useData(admUsers, enabled);
  const [picked, setPicked] = useState({});
  const [form, setForm] = useState({ fullName: "", email: "", password: "", role: "Employee" });
  const set = (k, v) => setForm((f) => ({ ...f, [k]: v }));

  const users = (Array.isArray(data) ? data : []).map(normalizeUser);

  const changeRole = (u) => {
    const role = picked[u.id] || u.roles[0] || "Employee";
    if (!window.confirm(`Change the role of ${u.email} to ${role}? All of their active sessions will be ended.`)) return null;
    return run(`r-${u.id}`, () => admSetRole(u.id, role), `${u.email} is now ${role}.`, "role", reload);
  };
  const unlock = (u) =>
    run(`u-${u.id}`, () => admUnlock(u.id), `${u.email} was unlocked.`, "unlock", reload);

  const create = (e) => {
    e.preventDefault();
    if (!form.fullName.trim() || !form.email.trim()) {
      return run("c-none", () => Promise.reject(new Error("Full name and email are required.")), "", "create");
    }
    if (form.password.length < 6) {
      return run("c-none", () => Promise.reject(new Error("The password must be at least 6 characters.")), "", "create");
    }
    return run(
      "c-add",
      () => admCreateUser({ fullName: form.fullName.trim(), email: form.email.trim(), password: form.password, role: form.role }),
      "User created.",
      "create",
      async () => { setForm({ fullName: "", email: "", password: "", role: "Employee" }); await reload(); }
    );
  };

  return (
    <section>
      <Toolbar title="Users" loading={loading} onReload={reload} />
      <p className="mgr-help">
        Changing a role revokes all of that user's refresh tokens, so they must sign in again. The last Admin cannot be demoted.
      </p>

      <h3>New user</h3>
      <form className="mgr-form row" onSubmit={create}>
        <label>Full name
          <input className="mgr-input" maxLength={200} value={form.fullName} onChange={(e) => set("fullName", e.target.value)} />
        </label>
        <label>Email
          <input className="mgr-input" type="email" maxLength={256} value={form.email} onChange={(e) => set("email", e.target.value)} />
        </label>
        <label>Password (min 6)
          <input className="mgr-input" type="password" maxLength={128} value={form.password} onChange={(e) => set("password", e.target.value)} />
        </label>
        <label>Role
          <select className="mgr-input" value={form.role} onChange={(e) => set("role", e.target.value)}>
            {ROLES.map((r) => <option key={r}>{r}</option>)}
          </select>
        </label>
        <button type="submit" className="mgr-btn ok" disabled={!!busy}>Create user</button>
      </form>

      <LoadState loading={loading} error={error} empty={users.length === 0} emptyText="No users found." />
      {users.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Status</th><th>Change role</th></tr></thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td>{u.name}{me && me.email === u.email && <span className="mgr-tag">you</span>}</td>
                  <td>{u.email}</td>
                  <td>{u.roles.length ? u.roles.map((r) => <Badge key={r} text={r} />) : "-"}</td>
                  <td>{u.locked ? <Badge text="Locked" /> : <Badge text="Active" />}</td>
                  <td className="mgr-actions">
                    <select className="mgr-input" value={picked[u.id] || u.roles[0] || "Employee"}
                      onChange={(e) => setPicked((p) => ({ ...p, [u.id]: e.target.value }))}>
                      {ROLES.map((r) => <option key={r}>{r}</option>)}
                    </select>
                    <button type="button" className="mgr-btn" disabled={!!busy} onClick={() => changeRole(u)}>Set role</button>
                    {u.locked && <button type="button" className="mgr-btn ok" disabled={!!busy} onClick={() => unlock(u)}>Unlock</button>}
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

/* ---------- Audit log ---------- */

function AuditTab({ enabled }) {
  const [f, setF] = useState({ action: "", entityType: "", entityId: "", userId: "", take: "100" });
  const [applied, setApplied] = useState(f);
  const loader = useCallback(() => admAudit(applied), [applied]);
  const { data, loading, error, reload } = useData(loader, enabled);
  const rows = Array.isArray(data) ? data : [];
  const set = (k, v) => setF((x) => ({ ...x, [k]: v }));

  return (
    <section>
      <Toolbar title="Audit log" loading={loading} onReload={reload} />
      <form className="mgr-form row" onSubmit={(e) => { e.preventDefault(); setApplied(f); }}>
        <label>Action<input className="mgr-input" value={f.action} onChange={(e) => set("action", e.target.value)} placeholder="e.g. Approve" /></label>
        <label>Entity type<input className="mgr-input" value={f.entityType} onChange={(e) => set("entityType", e.target.value)} placeholder="e.g. Invoice" /></label>
        <label>Entity id<input className="mgr-input" value={f.entityId} onChange={(e) => set("entityId", e.target.value)} /></label>
        <label>User id<input className="mgr-input" value={f.userId} onChange={(e) => set("userId", e.target.value)} /></label>
        <label>Max rows<input className="mgr-input" type="number" min="1" max="1000" value={f.take} onChange={(e) => set("take", e.target.value)} /></label>
        <button type="submit" className="mgr-btn">Apply filter</button>
      </form>
      <LoadState loading={loading} error={error} empty={rows.length === 0} emptyText="No audit entries found." />
      {rows.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>When (UTC)</th><th>User</th><th>Action</th><th>Entity</th><th>Details</th></tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>{when(r.createdAtUtc)}</td>
                  <td>{r.userEmail || r.userId || "-"}</td>
                  <td>{r.action}</td>
                  <td>{r.entityType}{r.entityId ? ` #${r.entityId}` : ""}</td>
                  <td className="mgr-small">{r.details || "-"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ---------- Email logs ---------- */

function EmailTab({ enabled }) {
  const [f, setF] = useState({ type: "", entityId: "", take: "50" });
  const [applied, setApplied] = useState(f);
  const loader = useCallback(() => admEmailLogs(applied), [applied]);
  const { data, loading, error, reload } = useData(loader, enabled);
  const rows = Array.isArray(data) ? data : [];
  const set = (k, v) => setF((x) => ({ ...x, [k]: v }));

  return (
    <section>
      <Toolbar title="Email logs" loading={loading} onReload={reload} />
      <form className="mgr-form row" onSubmit={(e) => { e.preventDefault(); setApplied(f); }}>
        <label>Type<input className="mgr-input" value={f.type} onChange={(e) => set("type", e.target.value)} placeholder="e.g. InvoiceReminder" /></label>
        <label>Entity id<input className="mgr-input" value={f.entityId} onChange={(e) => set("entityId", e.target.value)} /></label>
        <label>Max rows<input className="mgr-input" type="number" min="1" max="500" value={f.take} onChange={(e) => set("take", e.target.value)} /></label>
        <button type="submit" className="mgr-btn">Apply filter</button>
      </form>
      <LoadState loading={loading} error={error} empty={rows.length === 0} emptyText="No email logs found." />
      {rows.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>When (UTC)</th><th>To</th><th>Subject</th><th>Type</th><th>Entity</th><th>Status</th><th>Error</th></tr></thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id}>
                  <td>{when(r.createdAtUtc)}</td>
                  <td>{r.toEmail}</td>
                  <td className="mgr-small">{r.subject}</td>
                  <td>{r.type}</td>
                  <td>{r.entityType}{r.entityId ? ` #${r.entityId}` : ""}</td>
                  <td><Badge text={r.status} /></td>
                  <td className="mgr-small">{r.error || "-"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

/* ---------- Jobs ---------- */

function JobsTab({ run, busy, setResult, result }) {
  const go = (key, fn, label) =>
    run(key, async () => { const r = await fn(); setResult({ label, data: r }); return r; }, `${label} completed.`, "job");
  return (
    <section>
      <div className="mgr-toolbar"><h2>Background jobs</h2></div>
      <p className="mgr-help">
        These jobs normally run automatically on a schedule. You can also run them immediately from here.
      </p>
      <div className="mgr-actions" style={{ marginBottom: 12 }}>
        <button type="button" className="mgr-btn ok" disabled={!!busy} onClick={() => go("j-overdue", admRunOverdue, "Overdue check")}>Run overdue check</button>
        <button type="button" className="mgr-btn" disabled={!!busy} onClick={() => go("j-clean", admRunCleanup, "Token cleanup")}>Run token cleanup</button>
        <a className="mgr-btn" href="https://localhost:7210/hangfire" target="_blank" rel="noreferrer">Open Hangfire dashboard</a>
      </div>
      {result && (
        <div className="mgr-note ok">
          <strong>{result.label}</strong>
          <pre style={{ margin: "6px 0 0", whiteSpace: "pre-wrap" }}>{JSON.stringify(result.data, null, 2) || "(The server returned no content.)"}</pre>
        </div>
      )}
    </section>
  );
}

/* ---------- the page ---------- */

export default function AdminPage({ active, user }) {
  const [tab, setTab] = useState("users");
  const [busy, setBusy] = useState("");
  const [notice, setNotice] = useState(null);
  const [jobResult, setJobResult] = useState(null);

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
        <h1>Admin tools</h1>
        <p>Users, audit log, email logs and background jobs. This page is available to Admins only.</p>
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
        <div className={`mgr-note ${notice.type}`} role={notice.type === "err" ? "alert" : "status"}>{notice.text}</div>
      )}

      {tab === "users" && <UsersTab enabled={active} me={user} {...common} />}
      {tab === "audit" && <AuditTab enabled={active} />}
      {tab === "email" && <EmailTab enabled={active} />}
      {tab === "jobs" && <JobsTab result={jobResult} setResult={setJobResult} {...common} />}
    </div>
  );
}
