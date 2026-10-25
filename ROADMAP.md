# Roadmap

This sequence is planned, not promised. There are no delivery dates. Modularyn is in planning, nothing has been released, and G-01 is the first goal. Milestone completion requires evidence and acceptance; listing an outcome here does not claim it is implemented.

| Milestone | Planned outcome |
| --- | --- |
| M1 — G-01: Product and contract baseline | Establish the coverage register, contracts, names, profiles, ownership rules, threat models, dependency review, and initial acceptance cases. Make the repository foundation buildable and verifiable. |
| M2 — G-02: Foundation and secure application slice | Build a small, realistic multi-tenant application that exercises module composition, identity, tenant isolation, permissions, settings and features, persistence on PostgreSQL, the API and its clients, jobs and durable events, files and observability, and pass its real-provider and failure tests. |
| M3 — Website profile | Qualify an early website profile on the same contracts as the complete CMS. Define its supported subset and versioned content-package format for moving site content. |
| M4 — G-03: Administration and commercial lifecycle | Qualify the canonical React Admin with identity and accounts, tenancy and subscriptions, authorization-server management, payments, audit, localization and templates, privacy requests, operation limits and communications. |
| M5 — Commerce | Add a Community commerce module for physical and digital goods: product catalog, cart, checkout through Payments, orders and order notifications, with tax and shipping rates from provider connectors. Inventory, warehousing, shipping labels and accounting stay out of scope. |
| M6 — G-04: Complete CMS | Expand the website profile into the complete CMS, including content authoring, publishing, and the required management capabilities. |
| M7 — G-05: Module catalog and advanced integration | Complete the module catalog and advanced integration paths with declared ownership, dependency rules, and compatibility evidence. |
| M8 — G-06: Tooling and frontend breadth | Broaden development tooling and frontend support while preserving shared application contracts and documented profile boundaries. |
| M9 — G-07: Governed low-code and AI authoring | Add low-code and AI-assisted authoring with explicit permissions, review, traceability, and controlled execution. |
| M10 — G-08: Production and maintenance readiness | Demonstrate deployment, observability, upgrades, recovery, security maintenance, and operational readiness for supported profiles. |
| M11 — G-09: Comprehensive coverage audit | Audit the complete requirement and acceptance inventory against implementation and retained evidence. Resolve gaps before claiming comprehensive coverage. |

## Edition direction

Community is free under Apache-2.0, including commercial use, and is complete without paid modules. Standard licenses the paid modules for one person; Pro licenses the same modules for teams and organizations. Their source is maintained in Modularyn/modularyn-pro under PolyForm Noncommercial 1.0.0, with commercial rights requiring a separate license. There are no offers or prices yet. Editions package one technical catalog; they do not create competing implementations of the same domain or remove Community rights.

Five rules govern the direction of paid services and rights:

1. Every paid service has a self-hosted path at the edition that sells it.
2. No access to your applications or data requires a Modularyn account.
3. Moving between hosted and self-hosted loses nothing in either direction.
4. Paid rights are verified offline and outlive Modularyn's servers.
5. Ending a subscription ends only the service, never self-hosting of covered releases or any Community right.
