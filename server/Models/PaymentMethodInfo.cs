namespace RestaurantOrders.Models;

public static class PaymentMethodInfo
{
    public static int GetMyDataCode(PaymentMethod method) => method switch
    {
        PaymentMethod.DomesticBankAccount => 1,
        PaymentMethod.ForeignBankAccount => 2,
        PaymentMethod.Cash => 3,
        PaymentMethod.Check => 4,
        PaymentMethod.Credit => 5,
        PaymentMethod.WebBanking => 6,
        PaymentMethod.Card => 7,
        PaymentMethod.Iris => 8,
        _ => 3
    };

    public static string DisplayName(PaymentMethod method) => method switch
    {
        PaymentMethod.DomesticBankAccount => "Επαγγελματικός λογαριασμός πληρωμών ημεδαπής",
        PaymentMethod.ForeignBankAccount => "Επαγγελματικός λογαριασμός πληρωμών αλλοδαπής",
        PaymentMethod.Cash => "Μετρητά",
        PaymentMethod.Check => "Επιταγή",
        PaymentMethod.Credit => "Επί πιστώσει",
        PaymentMethod.WebBanking => "Web Banking",
        PaymentMethod.Card => "Κάρτα / POS",
        PaymentMethod.Iris => "IRIS",
        _ => "Μετρητά"
    };
}
