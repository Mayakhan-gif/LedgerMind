export function money(amount, currency) {
  try {
    return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(amount)
  } catch {
    return `${currency} ${Number(amount).toFixed(2)}`
  }
}

export const dateOnly = (s) => (s ? s.slice(0, 10) : '—')

// Aaj ki tareekh UTC me (overdue ka hisab backend ke saath milane ke liye)
export const todayUtc = () => new Date().toISOString().slice(0, 10)

// Aaj ki tareekh local (form ki default date ke liye)
export function todayLocal() {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}