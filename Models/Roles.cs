namespace LedgerMind.Models;

public static class Roles
{
    public const string Admin = "Admin";
    public const string FinanceManager = "FinanceManager";
    public const string Employee = "Employee";

    // [Authorize(Roles = ...)] ke liye: dono manager roles
    public const string Managers = Admin + "," + FinanceManager;

    public static readonly string[] All = { Admin, FinanceManager, Employee };
}
