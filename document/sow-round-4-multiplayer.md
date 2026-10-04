# Statement of Work (SOW) — Odyssey × Tbrain, Round 4 (Multiplayer)

> **Odyssey – Fine-tuning Video Game Data Collection**
>
> Signed 2026-09-30 by both parties.
>
> Source: `raw/[Final]Odyssey_Tbrain_SOW_Round_4_Multiplayer - 9_30_26, 7_46 AM.pdf`

## At a glance

| Item | Value |
|---|---|
| Company | Odyssey, Inc. — 102 University Ave, STE 2A, Palo Alto, CA 94301 |
| Contractor | Tbrain Inc. — 8 The Green STE B, Dover, DE 19901 |
| Volume | 20,000 hours, Multi-Agent Shared-State data |
| Duration | 12 weeks + 1 week setup (13 weeks) |
| Calibration batch | 5% (1,000 hrs) for approval before the remaining 19,000 hrs |
| Final delivery | by December 31st, 2026 |
| Data format | [Capture Spec v2](./videogame-data-capture-spec-v2.md) and [Multiplayer Capture Spec v3](./multiplayer-data-capture-spec-v3.md) |
| Review window | 3 business days per delivery; 1 cure opportunity |
| Governing law | State of Wyoming |

---

This Statement of Work ("SOW") is entered into by and between Odyssey, Inc. ("Company"), registered at 102 University Ave, STE 2A, Palo Alto, CA 94301 and Tbrain Inc. ("Contractor"), registered at 8 The Green STE B, Dover, DE 19901 and sets forth the terms and conditions under which Contractor will provide the Services described herein. This SOW constitutes the entire agreement between the parties with respect to the Services described herein, unless and until the parties enter into a separate master services agreement or professional services agreement, in which case such agreement shall govern to the extent of any conflict.

## 1. Scope of Services

During the term of this SOW, Contractor shall provide the Company with a collection of **20,000 hours of HUD-free RGB video and frame-aligned input metadata, and camera view/projection matrices** across titles.

Services include:

Contractor will help Company collect video game data consistent with section 2's Data Requirements. This will be very high quality, very clean data with a controlled action distribution across varying game titles with corresponding metadata collected over 12 weeks plus 1 week setup.

Company shall be responsible for ensuring timely response to Contractor on deliverables for feedback and iterations. Contractor shall not be responsible for delays or quality issues caused by Company downtime, delays, or errors in Company-provided feedback.

## 2. Data Requirements

Data collected for Company will meet the following requirements and be delivered in file type and format consistent with requirements defined in:

- [`[Odyssey] Videogame_Data_Capture_Spec_v2 .pdf`](./videogame-data-capture-spec-v2.md)
- [`[Odyssey] Multiplayer | Game Data Capture Spec v3.pdf`](./multiplayer-data-capture-spec-v3.md)

Data collection description for 20,000 hours:

1. **Multi-Agent Shared-State Data (20,000 hrs):**
   1. Contractor will collect synchronized multiplayer sessions with per-agent capture streams and cross-agent interaction labels.
   2. Start with two-player synchronized sessions before expanding to three or four agents.
   3. Capture per-agent video, camera metadata, input/action traces, and key bindings using Odyssey's existing canonical format where possible.
   4. Add shared event timelines: visibility, pursuit, collision, combat, cooperation, blocking, handoff, and shared-object interactions.
   5. Include perspective-consistency samples: the same event observed from multiple agent viewpoints.
   6. **Primary Deliverables:** per-agent videos/frames, `shared_events.csv`, `cross_agent_interactions.csv`, `shared_state_observations.csv`.

Contractor will collect synchronized multiplayer sessions with per-agent views, actions, and cross-agent events.

A distribution of gaming titles and actions for Multi-Agent shared-state data is to be determined by Contractor with Company's approval.

## 3. Exclusivity

All deliverables and task outputs specifically created and delivered to Company under this SOW and accepted by Company shall be exclusive to Company.

Notwithstanding the foregoing, Contractor retains the right to use the underlying methodology, tooling, and general, non-proprietary data utilized in the creation of the Work Product for the benefit of other clients, provided such use does not involve the direct reuse or redistribution of the specific deliverables or tasks delivered under this SOW.

## 4. Contractor Deliverables

- 20,000 total hours Multi-Agent Shared-State Data, HUD-free RGB video and frame-aligned input and camera view/projection matrices metadata across titles that conforms to Company's provided specification.
- 5% calibration batch (1,000 hours) for approval, prior to completing the remaining 19,000 hours.

Delivery may occur in batches, or as mutually agreed.

## 5. Timeline

- **Ramp-up period:** approximately one (1) week from SOW execution
- **Delivery:** by December 31st, 2026

| Phase | Week | Focus | Deliverables |
|---|---|---|---|
| **I: Project Setup, Kickoff & Calibration** | 1–2 | (1) Initiate kickoff with Odyssey, establish comms and set up meeting and delivery cadence<br>(2) Set up project, tool / code, game and scene distribution, gamer recruitment and QC process<br>(3) Feasibility assessment and development | (1) Game list feasibility and initial capture tool<br>(2) Initiate calibration batch (5% of total volume)<br>(3) 1,000 hr delivery (calibration) going into week 2<br>(4) Make adjustments based on calibration batch feedback |
| **II: Ramp** | 3–4 | (1) Finalize gamer recruitment QC process<br>(2) Finalize deliverable upload cadence | (1) Week 3 delivery 1,800 hrs<br>(2) Week 4 delivery 1,800 hrs<br>(3) Make adjustments based on calibration batch feedback |
| **III: Steady state** | 5–12 | (1) Maintain ad hoc communication (Slack) in conjunction with weekly sync meetings. Maintain delivery schedule.<br>(2) Rework & adjustments as needed | (1) Week 5 delivery 1,800 hrs<br>(2) Week 6–12 delivery 1,800 hrs per week |
| **IV: Wrap up** | 13 | (1) Wrap up final deliverables, review client feedback, make any requested adjustments, changes, rework to data within agreed-upon guidelines | (1) Wrap up final deliverables and final QA/QC<br>(2) Final drop 1,000 hrs |

Delivery timelines assume timely feedback on calibration and batch reviews by the Company. Delays in feedback may result in corresponding delivery extensions. Delivery batches are subject to change given feedback/discussion with the Company.

**Volume check:** 1,000 (calibration) + 1,800 × 10 (weeks 3–12) + 1,000 (final drop) = **20,000 hrs**.

## 6. Acceptance Criteria

A task shall be deemed accepted if it:

- **(A)** Conforms in all material respects to the agreed data requirements, structure, and formatting approved during the calibration phase; and
- **(B)** Is completed by qualified annotators in accordance with the agreed scope.

Company shall provide written notice of acceptance or rejection within **three (3) business days** of delivery, including specific, objective reasons for any rejection tied to the agreed deliverable(s).

For any rejected deliverable, Contractor shall have **one opportunity to cure** or re-perform the deliverable at no additional charge. Company shall provide written notice of acceptance or rejection within three (3) business days of re-delivery (including specific, objective reasons for any rejection tied to the agreed deliverable(s)). If the deliverable is not accepted after re-performance, it shall not be deemed accepted for payment.

The acceptable delivery standard and data scheme cannot change after the 2,000-QA sample is agreed to by Company.

Deliverable(s) not reviewed or rejected within the review period shall be **deemed accepted**.

Ownership and license rights shall transfer only upon acceptance and payment.

Company agrees that unaccepted deliverable(s) may not be used for internal benchmarking, model training, evaluation, or customer-facing purposes. Any use of unaccepted deliverable(s) by Company shall require a separate written agreement executed by both parties and may be subject to additional fees.

## 7. Intellectual Property

Upon acceptance and full payment, Company shall own the specific data outputs delivered under this SOW.

Contractor retains all right, title, and interest in its background intellectual property, including methodologies, processes, scoring frameworks, templates, annotation guidelines, and general know-how ("Background IP"). No ownership or license to Background IP is granted except as necessary for Company's internal use of the accepted deliverables.

Contractor represents that it has the right to provide the Services and that Services will be performed in a professional manner.

## 8. Confidentiality

Each party ("Receiving Party") agrees that all non-public, confidential, or proprietary information disclosed by the other party ("Disclosing Party") in connection with this SOW, whether disclosed orally, visually, electronically, or in writing, including but not limited to task content, task structure, evaluation results, quality annotations, reviewer rationales, methodologies, processes, tools, benchmarks, pricing, and business discussions (collectively, "Confidential Information"), shall be treated as confidential.

The Receiving Party agrees to:

- use Confidential Information solely for purposes of performing under this SOW;
- not disclose Confidential Information to any third party except to its employees, contractors, or affiliates who have a need to know and are bound by confidentiality obligations at least as protective as those herein; and
- protect Confidential Information using at least the same degree of care it uses to protect its own confidential information, but no less than a reasonable standard of care.

Confidential Information shall not include information that:

- (a) is or becomes publicly available through no fault of the Receiving Party;
- (b) was lawfully known to the Receiving Party prior to disclosure;
- (c) is independently developed by the Receiving Party without use of or reference to the Confidential Information; or
- (d) is rightfully received from a third party without restriction.

Upon termination or completion of this SOW, and upon written request, the Receiving Party shall promptly return or destroy the Disclosing Party's Confidential Information, except that one archival copy may be retained solely for legal or compliance purposes.

The obligations in this Section shall survive termination or expiration of this SOW for a period of five (5) years, or for so long as the information remains confidential, whichever is longer.

## 9. Term & Termination

This SOW shall commence upon the Effective Date and continue until completion of the Services, unless earlier terminated in accordance with the Agreement.

This SOW is intended as a limited pilot engagement and does not create any obligation for either party to enter into future statements of work.

## 10. Limitation of Liability

Neither party shall be liable for indirect, incidental, special, or consequential damages. Contractor's total liability under this SOW shall not exceed the total fees paid under this SOW.

## 11. Governing Agreement

This SOW shall be governed by the laws of the State of Wyoming, without regard to conflict of law principles. In the event of a conflict between this SOW and the Agreement, the Agreement shall control.

---

## Signatures

| | Odyssey Systems, Inc | Tbrain, Inc. |
|---|---|---|
| Name | Oliver Cameron | Michael Malfitani |
| Title | CEO | Head of Sales & GTM |
| Date | 2026-09-30 | 2026-09-30 |

### Audit trail

| Activity | Detail | Timestamp (UTC) |
|---|---|---|
| Sent | michael@tbrain.ai sent a signature request to Oliver Cameron (oliver@odyssey.systems) and Michael Malfitani (michael@tbrain.ai) | 2026/09/30 12:47:54 |
| Signed | Michael Malfitani (michael@tbrain.ai) | 2026/09/30 12:48:43 |
| Signed | Oliver Cameron (oliver@odyssey.systems) | 2026/09/30 14:34:49 |
| Completed | Signed by all signers | 2026/09/30 14:34:49 |
