namespace CRV.Core.Models;

public class SmtpSettings
{
    public string Host        { get; set; } = "";
    public int    Port        { get; set; } = 587;
    public string Username    { get; set; } = "";
    public string Password    { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public bool   UseSsl      { get; set; } = true;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(Username) &&
        !string.IsNullOrWhiteSpace(Password) &&
        !string.IsNullOrWhiteSpace(FromAddress);
}
