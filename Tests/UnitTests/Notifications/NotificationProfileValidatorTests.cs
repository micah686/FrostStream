using System.Text.Json;
using Shared.Messaging;
using Shared.Secrets;
using Shouldly;
using TUnit.Core;

namespace UnitTests.Notifications;

public sealed class NotificationProfileValidatorTests
{
    [Test]
    public void Provider_Allows_Secret_References_For_Its_Own_Key()
    {
        var provider = new NotificationProviderDto
        {
            ProviderKey = "smtp-home",
            ProviderKind = "email",
            NotifyConfig = JsonDocument.Parse("""
                {
                  "provider": "smtp",
                  "smtp": {
                    "host": "smtp.example.test",
                    "username": "micah",
                    "password": "secret://smtp-home/password",
                    "fromEmail": "frost@example.test"
                  }
                }
                """).RootElement.Clone()
        };

        NotificationProfileValidator.Validate(provider).ShouldBeNull();
    }

    [Test]
    public void Provider_Rejects_Secret_References_For_A_Different_Provider()
    {
        var provider = new NotificationProviderDto
        {
            ProviderKey = "smtp-home",
            ProviderKind = "email",
            NotifyConfig = JsonDocument.Parse("""
                {
                  "provider": "smtp",
                  "smtp": {
                    "password": "secret://other-provider/password"
                  }
                }
                """).RootElement.Clone()
        };

        NotificationProfileValidator.Validate(provider)
            .ShouldBe("Secret reference 'secret://other-provider/password' must use provider key 'smtp-home'.");
    }

    [Test]
    public void Notification_Secret_Path_Is_User_And_Provider_Scoped()
    {
        SecretPaths.ForUserNotificationProvider("auth0.user-1", "smtp-home")
            .ShouldBe("notifications/users/auth0.user-1/smtp-home");
        SecretPaths.ForUserNotificationSecret("auth0.user-1", "smtp-home", "password")
            .ShouldBe("notifications/users/auth0.user-1/smtp-home/password");
    }
}
