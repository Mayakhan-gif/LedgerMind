import './mobile.css'
import './forms.css'
import './extra.css'
import './dashboard.css'
import './integrations.css'
import { useEffect, useState } from 'react'
import Login from './components/Login'
import Layout from './components/Layout'
import DashboardPage from './components/DashboardPage'
import ChatPage from './components/ChatPage'
import InvoicesPage from './components/InvoicesPage'
import ExpensesPage from './components/ExpensesPage'
import TasksPage from './components/TasksPage'
import CustomersPage from './components/CustomersPage'
import AdminPage from './components/AdminPage'
import VendorsPage from './components/VendorsPage'
import IntegrationsPage from './components/IntegrationsPage'
import ManagerPage from './components/ManagerPage'
import { restoreSession, logout, setSessionExpiredHandler } from './api'

// Stripe payment ke baad apne success/cancel address par wapas bhejta hai.
// Ye sirf ek paigham dikhata hai. Invoice "Paid" tab hoga jab Stripe ka signed webhook server tak pahunche.
const returnPath = window.location.pathname
const PAYMENT_RETURN =
  returnPath === '/payment/success' ? 'success' : returnPath === '/payment/cancel' ? 'cancel' : null
if (PAYMENT_RETURN) window.history.replaceState({}, '', '/')

const PAYMENT_NOTICE =
  PAYMENT_RETURN === 'success'
    ? 'You are back from Stripe. The invoice will show as Paid only after Stripe confirms the payment to our server. This can take a few moments, so press Refresh to check.'
    : PAYMENT_RETURN === 'cancel'
      ? 'The payment was cancelled on Stripe’s page. The invoice has not been marked as paid.'
      : ''

export default function App() {
  const [user, setUser] = useState(null)
  const [booting, setBooting] = useState(true)
  const [page, setPage] = useState(PAYMENT_RETURN ? 'invoices' : 'dashboard')

  useEffect(() => {
    setSessionExpiredHandler(() => setUser(null))
    restoreSession()
      .then((u) => setUser(u))
      .finally(() => setBooting(false))
  }, [])

  async function handleLogout() {
    await logout()
    setUser(null)
    setPage('dashboard')
  }

  const isStaff = user?.roles?.some((r) => r === 'Admin' || r === 'FinanceManager')
  const isAdmin = user?.roles?.includes('Admin')

  return (
    <>
      <div className="aurora" />
      {booting ? (
        <div className="login-wrap"><span className="spinner" style={{ width: 28, height: 28 }} /></div>
      ) : user ? (
        <Layout user={user} page={page} onNavigate={setPage} onLogout={handleLogout}>
          <div className="page" hidden={page !== 'dashboard'}>
            <DashboardPage active={page === 'dashboard'} user={user} onNavigate={setPage} />
          </div>
          <div className="page" hidden={page !== 'chat'}><ChatPage /></div>
          <div className="page" hidden={page !== 'invoices'}>
            <InvoicesPage active={page === 'invoices'} user={user} initialNotice={PAYMENT_NOTICE} />
          </div>
          <div className="page" hidden={page !== 'expenses'}>
            <ExpensesPage active={page === 'expenses'} user={user} />
          </div>
          <div className="page" hidden={page !== 'tasks'}>
            <TasksPage active={page === 'tasks'} user={user} />
          </div>
          <div className="page" hidden={page !== 'customers'}>
            <CustomersPage active={page === 'customers'} user={user} />
          </div>
          {isStaff && (
            <div className="page" hidden={page !== 'vendors'}>
              <VendorsPage active={page === 'vendors'} />
            </div>
          )}
          {isStaff && (
            <div className="page" hidden={page !== 'integrations'}>
              <IntegrationsPage active={page === 'integrations'} user={user} onNavigate={setPage} />
            </div>
          )}
          {isStaff && (
            <div className="page" hidden={page !== 'manager'}>
              <ManagerPage active={page === 'manager'} user={user} />
            </div>
          )}
          {isAdmin && (
            <div className="page" hidden={page !== 'admin'}>
              <AdminPage active={page === 'admin'} user={user} />
            </div>
          )}
        </Layout>
      ) : (
        <Login onLogin={setUser} />
      )}
    </>
  )
}