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
              "type": "system",
              "relations": {
                "owner": { "this": {} },
                "admin": {
                  "union": {
                    "child": [
                      { "this": {} },
                      { "computedUserset": { "relation": "owner" } }
                    ]
                  }
                },
                "member": {
                  "union": {
                    "child": [
                      { "this": {} },
                      { "computedUserset": { "relation": "admin" } }
                    ]
                  }
                },
                "viewer": {
                  "union": {
                    "child": [
                      { "this": {} },
                      { "computedUserset": { "relation": "member" } }
                    ]
                  }
                },
                "access": { "computedUserset": { "relation": "viewer" } },
                "manage": { "computedUserset": { "relation": "admin" } }
              },
              "metadata": {
                "relations": {
                  "owner": {
                    "directly_related_user_types": [
                      { "type": "user" },
                      { "type": "group", "relation": "member" }
                    ]
                  },
                  "admin": {
                    "directly_related_user_types": [
                      { "type": "user" },
                      { "type": "group", "relation": "member" }
                    ]
                  },
                  "member": {
                    "directly_related_user_types": [
                      { "type": "user" },
                      { "type": "group", "relation": "member" }
                    ]
                  },
                  "viewer": {
                    "directly_related_user_types": [
                      { "type": "user" },
                      { "type": "group", "relation": "member" }
                    ]
                  },
                  "access": { "directly_related_user_types": [] },
                  "manage": { "directly_related_user_types": [] }
                }
              }
            }
          ]
        }
        """;
}
