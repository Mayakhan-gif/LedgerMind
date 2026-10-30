import { useCallback, useEffect, useState } from "react";
import { venList, venCreate, venUpdate, venDelete } from "../api";
import "../manager.css";

function withTimeout(promise, ms = 25000) {
  let timer;
  const limit = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error("The server did not respond in time. Is the backend running?")), ms);
  });
  return Promise.race([promise, limit]).finally(() => clearTimeout(timer));
}

const day = (v) => (v ? String(v).slice(0, 10) : "-");

export default function VendorsPage({ active }) {
  const [list, setList] = useState(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [notice, setNotice] = useState(null);
  const [busy, setBusy] = useState("");
  const [editId, setEditId] = useState(null); // null = new vendor
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    setError("");
    try {
      setList(await withTimeout(venList()));
    } catch (e) {
      setError(e.message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (active) load();
  }, [active, load]);

  const reset = () => { setEditId(null); setName(""); setEmail(""); };

  const submit = async (e) => {
    e.preventDefault();
    setNotice(null);
    if (!name.trim()) return setNotice({ type: "err", text: "Vendor name is required." });
    if (email.trim() && !/^\S+@\S+\.\S+$/.test(email.trim())) return setNotice({ type: "err", text: "Enter a valid email address." });
    const body = { name: name.trim(), email: email.trim() || null };
    setBusy("save");
    try {
      if (editId == null) await withTimeout(venCreate(body));
      else await withTimeout(venUpdate(editId, body));
      setNotice({ type: "ok", text: editId == null ? "Vendor added." : "Vendor updated." });
      reset();
      await load();
    } catch (err) {
      setNotice({ type: "err", text: err.status === 403 ? "Your role does not have permission for this action." : err.message });
    } finally {
      setBusy("");
    }
  };

  const remove = async (v) => {
    if (!window.confirm(`Delete vendor "${v.name}"?`)) return;
    setNotice(null);
    setBusy(`del-${v.id}`);
    try {
      await withTimeout(venDelete(v.id));
      setNotice({ type: "ok", text: `"${v.name}" was deleted.` });
      if (editId === v.id) reset();
      await load();
    } catch (err) {
      const msg = err.status === 400 || err.status === 409
        ? `"${v.name}" cannot be deleted because it is still in use. [${err.message}]`
        : err.status === 403 ? "Your role does not have permission for this action." : err.message;
      setNotice({ type: "err", text: msg });
    } finally {
      setBusy("");
    }
  };

  const rows = Array.isArray(list) ? list : [];

  return (
    <div className="mgr">
      <div className="mgr-head">
        <h1>Vendors</h1>
        <p>The suppliers you purchase from. Add, edit or remove them here.</p>
      </div>

      {notice && <div className={`mgr-note ${notice.type}`} role={notice.type === "err" ? "alert" : "status"}>{notice.text}</div>}

      <h3>{editId == null ? "New vendor" : "Edit vendor"}</h3>
      <form className="mgr-form row" onSubmit={submit}>
        <label>Name
          <input className="mgr-input" maxLength={200} value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>Email (optional)
          <input className="mgr-input" type="email" maxLength={200} value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <button type="submit" className="mgr-btn ok" disabled={!!busy}>{editId == null ? "Add vendor" : "Save changes"}</button>
        {editId != null && <button type="button" className="mgr-btn" onClick={reset}>Cancel</button>}
      </form>

      <div className="mgr-toolbar">
        <h2>All vendors ({rows.length})</h2>
        <button type="button" className="mgr-btn" onClick={load} disabled={loading}>{loading ? "Loading..." : "Refresh"}</button>
      </div>
      {error && <div className="mgr-note err" role="alert">{error}</div>}
      {!error && !loading && rows.length === 0 && <div className="mgr-note">No vendors yet.</div>}
      {rows.length > 0 && (
        <div className="mgr-table-wrap">
          <table className="mgr-table">
            <thead><tr><th>Name</th><th>Email</th><th>Added</th><th></th></tr></thead>
            <tbody>
              {rows.map((v) => (
                <tr key={v.id}>
                  <td>{v.name}</td>
                  <td>{v.email || "-"}</td>
                  <td>{day(v.createdAtUtc)}</td>
                  <td className="mgr-actions">
                    <button type="button" className="mgr-btn" disabled={!!busy}
                      onClick={() => { setNotice(null); setEditId(v.id); setName(v.name || ""); setEmail(v.email || ""); }}>Edit</button>
                    <button type="button" className="mgr-btn danger" disabled={!!busy} onClick={() => remove(v)}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
