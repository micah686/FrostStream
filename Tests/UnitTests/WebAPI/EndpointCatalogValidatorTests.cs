using TUnit.Core;
using WebAPI.Auth;

namespace UnitTests.WebAPI;

public sealed class EndpointCatalogValidatorTests
{
    [Test]
    public void Catalog_Has_Exactly_One_Route_For_Every_Endpoint_Id()
        => EndpointCatalogValidator.Validate(typeof(global::WebAPI.Program).Assembly);
}
