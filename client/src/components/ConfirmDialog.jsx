import { useEffect, useRef } from 'react'

export default function ConfirmDialog({ title, message, confirmLabel, danger, busy, onConfirm, onCancel }) {
  const cancelRef = useRef(null)

  useEffect(() => {
    cancelRef.current?.focus()
    const onKey = (e) => e.key === 'Escape' && !busy && onCancel()
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [busy, onCancel])

  return (
    <div className="overlay" onClick={() => !busy && onCancel()}>
      <div className="card dialog" role="dialog" aria-modal="true" aria-labelledby="dlg-title"
        onClick={(e) => e.stopPropagation()}>
        <h3 id="dlg-title">{title}</h3>
        <p className="muted">{message}</p>
        <div className="dialog-actions">
          <button ref={cancelRef} className="btn-ghost" onClick={onCancel} disabled={busy}>Cancel</button>
          <button className={danger ? 'btn-danger' : 'btn-primary btn-sm'} onClick={onConfirm} disabled={busy}>
            {busy ? <span className="spinner" /> : confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}