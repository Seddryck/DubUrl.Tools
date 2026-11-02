# Connector catalog support

`Catalog` derives Application's default provider metadata from the pinned provider NuGet packages and `connector-matrix.json`. Metadata compatibility/version tracks `DubUrlVersion`, independently of Tools' release version.

The CLI distribution includes the separately pinned official catalog from the Core GitHub Release selected by `ConnectorCatalogVersion`. Core owns connector pack building and publication; Tools consumes these assets without republishing or renaming them.

`Signing` hosts Application's catalog publication/signing API. Run its examples from this repository; see [catalog distribution and signing](https://github.com/Seddryck/DubUrl/blob/main/connectors/DISTRIBUTION.md). Private keys and passwords are external runtime inputs.
