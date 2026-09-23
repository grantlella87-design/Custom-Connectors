These are the artifacts required to register the National Grid GIS Enhanced V13 Print connector with Power Platform APIM.

- `apiDefinition.swagger.json` - OpenAPI definition of the V13 connector (version 13.0).
- `apiProperties.json` - connection parameters (on-premises data gateway) and connector policies.
- `settings.json` - `paconn` settings for the target environment.

`settings.json` intentionally has no `connectorId`: the one previously stored here belonged to the V10
connector, and `paconn update` with it would overwrite V10. Either create a new connector:

```
paconn create --settings settings.json
```

or add the V13 connector's ID (`"connectorId": "shared_..."`) and run `paconn update --settings settings.json`.
