using System.Net;
using System.Text.Json.Nodes;

namespace HandyNet.IntegrationTests.Infrastructure;

internal static class EmailLanguageExtensions
{
    /// <summary>Sets the email language of the client's organization through the company profile endpoint (keeps all other fields).</summary>
    public static async Task SetEmailLanguageAsync(this ApiClient owner, string language)
    {
        var profile = (await owner.GetAsync("/api/organization/profile")).Expect(HttpStatusCode.OK).Json;
        var request = new JsonObject();
        foreach (var name in new[]
                 {
                     "name", "legalName", "street", "postalCode", "city", "countryCode", "vatId", "taxNumber", "contactEmail", "contactPhone",
                     "website", "receiptHeader", "receiptFooter", "bankName", "iban", "bic", "managingDirector", "registerCourt", "registerNumber",
                     "paymentTermsDays", "version",
                 })
            request[name] = profile[name]?.DeepClone();
        request["emailLanguage"] = language;
        (await owner.PutAsync("/api/organization/profile", request)).Expect(HttpStatusCode.OK);
    }
}
