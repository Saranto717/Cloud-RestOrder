namespace RestaurantOrders.Models;

public class PrinterStation
{
    public int Id { get; set; }
    public string Name { get; set; } = "";      // π.χ. "Κουζίνα", "Μπαρ"
    public string ConnectionType { get; set; } = "Tcp"; // Tcp, Usb (Windows spooler) ή Sunmi (Android)
    public string WindowsPrinterName { get; set; } = ""; // Όνομα ουράς Windows για USB ESC/POS
    public bool PrintOrders { get; set; } = true;
    public bool PrintFiscalDocuments { get; set; } = true;
    public string IpAddress { get; set; } = ""; // π.χ. 192.168.1.50
    public int Port { get; set; } = 9100;       // τυπικό RAW/ESC-POS port
    public int Copies { get; set; } = 1;        // πόσα αντίγραφα να τυπώνει (διπλότυπα/τριπλότυπα)
    public string Codepage { get; set; } = "PC737"; // "PC737" ή "1253" - δοκίμασε το άλλο αν βγαίνουν λάθος ελληνικά
    public int PaperWidthChars { get; set; } = 42;  // χαρακτήρες ανά γραμμή (32 για 58mm, 42-48 για 80mm χαρτί)
    public bool IsActive { get; set; } = true;
}
