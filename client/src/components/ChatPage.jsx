import { useEffect, useRef, useState } from 'react'
import { sendMessage, DEMO_MODE } from '../api'
import { SendIcon, PlusIcon, SparkIcon } from './Icons'

const SUGGESTIONS = [
  'Show overdue invoices',
  'Which approvals are waiting for me?',
  'Summarize this month’s expenses',
  'Show my pending tasks',
]

const HISTORY_LIMIT = 10

export default function ChatPage() {
  const [messages, setMessages] = useState([])
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const endRef = useRef(null)

  useEffect(() => {
    endRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, busy])

  async function send(text) {
    const clean = text.trim()
    if (!clean || busy) return

    // Pichli baat (naya sawal chhor kar), error wale messages nahi
    const history = messages
      .filter((m) => !m.isError)
      .slice(-HISTORY_LIMIT)
      .map((m) => ({ role: m.role, content: m.text }))

    setMessages((m) => [...m, { role: 'user', text: clean }])
    setInput('')
    setBusy(true)
    try {
      const reply = await sendMessage(clean, history)
      setMessages((m) => [...m, { role: 'assistant', text: reply }])
    } catch (err) {
      setMessages((m) => [...m, { role: 'assistant', text: err.message, isError: true }])
    } finally {
      setBusy(false)
    }
  }

  function onKeyDown(e) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      send(input)
    }
  }

  return (
    <main className="chat-main">
      <header className="chat-head">
        <h2>Assistant</h2>
        {DEMO_MODE && <span className="demo-badge">Demo mode</span>}
        <button className="btn-ghost new-chat" onClick={() => setMessages([])} disabled={busy}>
          <PlusIcon /> New chat
        </button>
      </header>

      <div className="messages">
        {messages.length === 0 ? (
          <div className="hero">
            <div className="logo big"><SparkIcon /></div>
            <h1>How can I help today?</h1>
            <p className="muted">Ask about invoices, expenses, tasks and approvals.</p>
            <div className="chips">
              {SUGGESTIONS.map((s, i) => (
                <button key={s} className="chip" style={{ animationDelay: `${i * 80}ms` }} onClick={() => send(s)}>
                  {s}
                </button>
              ))}
            </div>
          </div>
        ) : (
          messages.map((m, i) => (
            <div key={i} className={`msg ${m.role}${m.isError ? ' err' : ''}`}>
              {m.role === 'assistant' && <span className="logo sm"><SparkIcon /></span>}
              <div className="bubble">{m.text}</div>
            </div>
          ))
        )}

        {busy && (
          <div className="msg assistant">
            <span className="logo sm"><SparkIcon /></span>
            <div className="bubble typing"><i /><i /><i /></div>
          </div>
        )}
        <div ref={endRef} />
      </div>

      <div className="composer">
        <textarea rows={1} value={input} placeholder="Ask anything about your finances..."
          onChange={(e) => setInput(e.target.value)} onKeyDown={onKeyDown} />
        <button className="send" onClick={() => send(input)} disabled={!input.trim() || busy} aria-label="Send">
          <SendIcon />
        </button>
      </div>
    </main>
  )
}