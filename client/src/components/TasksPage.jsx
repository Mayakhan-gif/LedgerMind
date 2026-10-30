import { useEffect, useMemo, useRef, useState } from 'react'
import { getTasks, createTask, updateTask, setTaskStatus, deleteTask } from '../api'
import { dateOnly, todayUtc } from '../format'
import ConfirmDialog from './ConfirmDialog'

const STATUSES = ['Todo', 'InProgress', 'Done']
const PRIORITIES = ['Low', 'Medium', 'High']
const LABELS = { Todo: 'To do', InProgress: 'In progress' }
const label = (s) => LABELS[s] || s

function isOverdue(t) {
  return t.status !== 'Done' && !!t.dueDate && dateOnly(t.dueDate) < todayUtc()
}

function TaskForm({ task, onCancel, onSaved }) {
  const editing = !!task
  const [title, setTitle] = useState(task?.title || '')
  const [description, setDescription] = useState(task?.description || '')
  const [priority, setPriority] = useState(task?.priority || 'Medium')
  const [dueDate, setDueDate] = useState(task?.dueDate ? task.dueDate.slice(0, 10) : '')
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
    if (!title.trim()) v.title = 'Title is required.'
    else if (title.trim().length > 200) v.title = 'Use 200 characters or fewer.'
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
      description: description.trim() || null,
      priority,
      // Dopahar UTC: backend UTC me badalta hai, is se tareekh ek din peeche nahi jaati
      dueDate: dueDate ? `${dueDate}T12:00:00Z` : null,
    }

    setSaving(true)
    setSubmitError('')
    try {
      if (editing) await updateTask(task.id, payload)
      else await createTask(payload)
      onSaved(editing ? 'updated' : 'created')
    } catch (err) {
      setSubmitError(
        err.status === 400 ? 'Could not save. Please check the values and try again.' : err.message,
      )
      setSaving(false)
    }
  }

  return (
    <div className="overlay" onClick={() => !saving && onCancel()}>
      <form className="card dialog" role="dialog" aria-modal="true" aria-labelledby="task-title"
        onClick={(e) => e.stopPropagation()} onSubmit={handleSubmit} noValidate>
        <h3 id="task-title">{editing ? 'Edit task' : 'New task'}</h3>

        <label>
          Title
          <input ref={firstRef} value={title} onChange={(e) => setTitle(e.target.value)} aria-invalid={!!errors.title} />
          {errors.title && <span className="field-error">{errors.title}</span>}
        </label>

        <div className="form-grid">
          <label>
            Priority
            <select value={priority} onChange={(e) => setPriority(e.target.value)}>
              {PRIORITIES.map((p) => <option key={p} value={p}>{p}</option>)}
            </select>
          </label>
          <label>
            Due date (optional)
            <input type="date" value={dueDate} onChange={(e) => setDueDate(e.target.value)} />
          </label>
        </div>

        <label>
          Description (optional)
          <textarea rows={3} value={description} onChange={(e) => setDescription(e.target.value)}
            aria-invalid={!!errors.description} />
          {errors.description && <span className="field-error">{errors.description}</span>}
        </label>

        {!editing && <p className="muted" style={{ fontSize: 12 }}>New tasks are assigned to you.</p>}
        {submitError && <p className="error banner" role="alert">{submitError}</p>}

        <div className="dialog-actions">
          <button type="button" className="btn-ghost" onClick={onCancel} disabled={saving}>Cancel</button>
          <button type="submit" className="btn-primary btn-sm" disabled={saving}>
            {saving ? <span className="spinner" /> : editing ? 'Save changes' : 'Add task'}
          </button>
        </div>
      </form>
    </div>
  )
}

export default function TasksPage({ active, user }) {
  const [tasks, setTasks] = useState(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [query, setQuery] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [form, setForm] = useState(null) // null | { task? }
  const [toDelete, setToDelete] = useState(null)
  const [busyId, setBusyId] = useState(null)
  const [deleting, setDeleting] = useState(false)
  const [actionError, setActionError] = useState('')
  const [notice, setNotice] = useState('')

  const isManager = user.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')
  const canEdit = (t) => isManager || t.createdByUserId === user.id
  const canChangeStatus = (t) => isManager || t.assignedToUserId === user.id

  async function load() {
    setLoading(true)
    setError('')
    try {
      setTasks(await getTasks())
    } catch (err) {
      setError(err.message)
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (active) load()
  }, [active])

  async function changeStatus(task, status) {
    setBusyId(task.id)
    setActionError('')
    setNotice('')
    try {
      const updated = await setTaskStatus(task.id, status)
      setTasks((list) => list.map((t) => (t.id === task.id ? updated : t)))
    } catch (err) {
      setActionError(err.message)
      load()
    } finally {
      setBusyId(null)
    }
  }

  async function remove(task) {
    setDeleting(true)
    setActionError('')
    try {
      await deleteTask(task.id)
      setTasks((list) => list.filter((t) => t.id !== task.id))
      setNotice('Task deleted.')
    } catch (err) {
      setActionError(err.message)
      load()
    } finally {
      setDeleting(false)
      setToDelete(null)
    }
  }

  function handleSaved(kind) {
    setForm(null)
    setNotice(kind === 'created' ? 'Task added.' : 'Task updated.')
    load()
  }

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return (tasks || [])
      .filter(
        (t) =>
          (!statusFilter || t.status === statusFilter) &&
          (!q || t.title.toLowerCase().includes(q) || (t.description || '').toLowerCase().includes(q)),
      )
      .sort(
        (a, b) =>
          (a.status === 'Done') - (b.status === 'Done') ||
          (a.dueDate || '9999').localeCompare(b.dueDate || '9999'),
      )
  }, [tasks, query, statusFilter])

  const stats = useMemo(() => {
    const list = tasks || []
    return {
      open: list.filter((t) => t.status !== 'Done').length,
      overdue: list.filter(isOverdue).length,
      done: list.filter((t) => t.status === 'Done').length,
    }
  }, [tasks])

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Tasks</h2>
        <div className="head-actions">
          <button className="btn-ghost btn-sm" onClick={load} disabled={loading}>Refresh</button>
          <button className="btn-primary btn-sm" onClick={() => { setNotice(''); setForm({}) }}>New task</button>
        </div>
      </header>

      <div className="page-body">
        {tasks && (
          <div className="stat-row">
            <div className="card stat"><span className="muted">Open</span><strong>{stats.open}</strong></div>
            <div className="card stat"><span className="muted">Overdue</span><strong>{stats.overdue}</strong></div>
            <div className="card stat"><span className="muted">Done</span><strong>{stats.done}</strong></div>
          </div>
        )}

        <div className="toolbar">
          <input type="search" placeholder="Search tasks" value={query}
            onChange={(e) => setQuery(e.target.value)} aria-label="Search tasks" />
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

        {loading && !tasks && <div className="center-pad"><span className="spinner" style={{ width: 26, height: 26 }} /></div>}

        {tasks && filtered.length === 0 && (
          <div className="center-pad muted">
            {tasks.length === 0 ? 'No tasks yet. Add the first one.' : 'No tasks match your search.'}
          </div>
        )}

        {filtered.length > 0 && (
          <div className="card table-wrap">
            <table>
              <thead>
                <tr><th>Task</th><th>Priority</th><th>Due date</th><th>Status</th><th /></tr>
              </thead>
              <tbody>
                {filtered.map((t) => (
                  <tr key={t.id}>
                    <td>
                      <strong>{t.title}</strong>
                      {t.description && <span className="sub">{t.description}</span>}
                    </td>
                    <td><span className={`badge p-${t.priority.toLowerCase()}`}>{t.priority}</span></td>
                    <td>
                      {dateOnly(t.dueDate)}
                      {isOverdue(t) && <span className="tag tag-red">Overdue</span>}
                    </td>
                    <td>
                      {canChangeStatus(t) ? (
                        <select className="row-select" value={t.status} disabled={busyId === t.id}
                          onChange={(e) => changeStatus(t, e.target.value)} aria-label={`Status of ${t.title}`}>
                          {STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}
                        </select>
                      ) : (
                        <span className={`badge s-${t.status.toLowerCase()}`}>{label(t.status)}</span>
                      )}
                    </td>
                    <td className="actions">
                      {canEdit(t) && (
                        <>
                          <button className="btn-ghost btn-sm" onClick={() => { setNotice(''); setForm({ task: t }) }}>Edit</button>
                          <button className="btn-ghost btn-sm" onClick={() => setToDelete(t)}>Delete</button>
                        </>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {form && <TaskForm task={form.task} onCancel={() => setForm(null)} onSaved={handleSaved} />}

      {toDelete && (
        <ConfirmDialog
          title="Delete this task?"
          message={`"${toDelete.title}" will be permanently deleted.`}
          confirmLabel="Delete"
          danger
          busy={deleting}
          onConfirm={() => remove(toDelete)}
          onCancel={() => setToDelete(null)}
        />
      )}
    </main>
  )
}