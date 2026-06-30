namespace WebAPI.Auth;

/// <summary>
/// The FrostStream OpenFGA authorization model, expressed as the JSON body accepted by the
/// WriteAuthorizationModel API. The equivalent DSL lives at
/// <c>AppHost/configs/openfga/model.fga</c>; keep the two in sync.
/// </summary>
public static class OpenFgaModel
{
    public const string SchemaVersion = "1.1";

    public const string Json = """
        {
          "schema_version": "1.1",
          "type_definitions": [
            {
              "type": "user"
            },
            {
              "type": "group",
              "relations": {
                "member": { "this": {} }
              },
              "metadata": {
                "relations": {
                  "member": {
                    "directly_related_user_types": [ { "type": "user" } ]
                  }
                }
              }
            },
            {
              "type": "capability_group",
              "relations": {
                "grantee": { "this": {} }
              },
              "metadata": {
                "relations": {
                  "grantee": {
                    "directly_related_user_types": [
                      { "type": "user" },
                      { "type": "group", "relation": "member" }
                    ]
                  }
                }
              }
            },
            {
              "type": "endpoint",
              "relations": {
                "bundle": { "this": {} },
                "invoke": {
                  "union": {
                    "child": [
                      { "this": {} },
                      {
                        "tupleToUserset": {
                          "tupleset": { "relation": "bundle" },
                          "computedUserset": { "relation": "grantee" }
                        }
                      }
                    ]
                  }
                }
              },
              "metadata": {
                "relations": {
                  "bundle": {
                    "directly_related_user_types": [
                      { "type": "capability_group" }
                    ]
                  },
                  "invoke": {
                    "directly_related_user_types": [
                      { "type": "user" },
                      { "type": "group", "relation": "member" }
                    ]
                  }
                }
              }
            }
          ]
        }
        """;
}
