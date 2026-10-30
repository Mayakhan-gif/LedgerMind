using System.Linq.Expressions;
using LedgerMind.Models;

namespace LedgerMind.Services;

// Invoice ke qaide ek jagah: reports, background job, chatbot aur payments sab yahi use karte hain
public static class InvoiceRules
{
    // Jin statuses par paisa liya ja sakta hai
    public static readonly string[] CollectableStatuses = { InvoiceStatus.Approved, InvoiceStatus.Partial };

    // Reports me ginne wale (approve ho chuke) invoices
    public static readonly string[] ReportableStatuses =
        { InvoiceStatus.Approved, InvoiceStatus.Partial, InvoiceStatus.Paid };

    // EF query ke andar chalne wala overdue qaida
    public static Expression<Func<Invoice, bool>> IsOverdue(DateTime todayUtc) =>
        i => !i.IsExcluded &&
             i.DueDate < todayUtc &&
             (i.Status == InvoiceStatus.Approved || i.Status == InvoiceStatus.Partial);

    public static bool IsCollectable(Invoice invoice) =>
        !invoice.IsExcluded &&
        (invoice.Status == InvoiceStatus.Approved || invoice.Status == InvoiceStatus.Partial);

    // Kul jama raqam ke hisab se status (sirf collectable/paid invoices ke liye)
    public static string StatusForPaid(decimal total, decimal paid)
    {
        if (paid >= total)
            return InvoiceStatus.Paid;
        if (paid > 0)
            return InvoiceStatus.Partial;
        return InvoiceStatus.Approved;
    }

    // Stripe minor units (sirf 2-decimal currencies, Currencies.Supported dekhein)
    public static long ToMinorUnits(decimal amount) =>
        (long)decimal.Round(amount * 100, 0, MidpointRounding.AwayFromZero);

    public static decimal FromMinorUnits(long minor) => minor / 100m;
}
