import {
  SparkIcon, LogoutIcon, HomeIcon, ChatIcon, ReceiptIcon, WalletIcon, TaskIcon, UsersIcon, PlugIcon,
} from './Icons'

const NAV = [
  { id: 'dashboard', label: 'Dashboard', Icon: HomeIcon },
  { id: 'chat', label: 'Assistant', Icon: ChatIcon },
  { id: 'invoices', label: 'Invoices', Icon: ReceiptIcon },
  { id: 'expenses', label: 'Expenses', Icon: WalletIcon },
  { id: 'tasks', label: 'Tasks', Icon: TaskIcon },
  { id: 'customers', label: 'Customers', Icon: UsersIcon },
  { id: 'vendors', label: 'Vendors', Icon: UsersIcon, staffOnly: true },
  { id: 'integrations', label: 'Integrations', Icon: PlugIcon, staffOnly: true },
  { id: 'manager', label: 'Manager tools', Icon: TaskIcon, staffOnly: true },
  { id: 'admin', label: 'Admin tools', Icon: PlugIcon, adminOnly: true },
]

export default function Layout({ user, page, onNavigate, onLogout, children }) {
  const displayName = user.fullName || user.email || 'User'
  const isStaff = user.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')
  const isAdmin = user.roles?.includes('Admin')
  const items = NAV.filter((item) => (!item.staffOnly || isStaff) && (!item.adminOnly || isAdmin))

  return (
    <div className="chat-layout">
      <aside className="sidebar">
        <div className="brand"><span className="logo sm"><SparkIcon /></span>LedgerMind</div>

        <nav className="nav" aria-label="Main">
          {items.map(({ id, label, Icon }) => (
            <button key={id} className={`nav-item${page === id ? ' active' : ''}`}
              aria-current={page === id ? 'page' : undefined} onClick={() => onNavigate(id)}>
              <Icon /> {label}
            </button>
          ))}
        </nav>

        <div className="sidebar-foot">
          <div className="avatar">{displayName[0].toUpperCase()}</div>
          <div className="who"><strong>{displayName}</strong><span className="muted">{user.email}</span></div>
          <button className="icon-btn" onClick={onLogout} aria-label="Sign out"><LogoutIcon /></button>
        </div>
      </aside>

      {children}
    </div>
  )
}