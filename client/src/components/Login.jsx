import { useState } from 'react'
import { login, DEMO_MODE } from '../api'
import { SparkIcon } from './Icons'

export default function Login({ onLogin }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  async function handleSubmit(e) {
    e.preventDefault()
    setError('')
    if (!email || !password) {
      setError('Email and password are required.')
      return
    }
    setLoading(true)
    try {
      onLogin(await login(email, password))
    } catch (err) {
      setError(err.message)
      setLoading(false)
    }
  }

  return (
    <div className="login-wrap">
      <form className="card login-card" onSubmit={handleSubmit}>
        <div className="logo"><SparkIcon /></div>
        <h1>Welcome back</h1>
        <p className="muted">Sign in to LedgerMind</p>

        <label>
          Email
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)}
            placeholder="you@company.com" autoComplete="username" />
        </label>
        <label>
          Password
          <input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
            placeholder="••••••••" autoComplete="current-password" />
        </label>

        {error && <p className="error" role="alert">{error}</p>}

        <button className="btn-primary" disabled={loading}>
          {loading ? <span className="spinner" /> : 'Sign in'}
        </button>
        {DEMO_MODE && <span className="demo-badge">Demo mode</span>}
      </form>
    </div>
  )
}