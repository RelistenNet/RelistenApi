// The operator page has no browser dependencies. Exercise its HTTP and DOM boundaries
// with inert text nodes so target identity, retries and preview invalidation stay observable.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";
import { test } from "node:test";

class Element {
  constructor(tag = "div") {
    this.tagName = tag.toUpperCase();
    this.value = "";
    this.textContent = "";
    this.children = [];
    this.attributes = {};
    this.listeners = {};
    this.classList = { toggle() {} };
  }
  set innerHTML(_) { throw new Error("Untrusted HTML rendering"); }
  addEventListener(name, callback) { this.listeners[name] = callback; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  setAttribute(name, value) { this.attributes[name] = value; }
  getAttribute(name) { return this.attributes[name]; }
  querySelectorAll() { return this.children; }
  reportValidity() { return true; }
  focus() {}
  scrollIntoView() {}
}

function fixture(search = "?domain=compositions&namespace=repertoire&stableKey=%5B%22original%22%2C%22song%3A123%22%5D&ignored=secret") {
  const elements = new Map();
  const selections = { action: "accept", scope: "account" };
  const document = {
    getElementById(id) {
      if (!elements.has(id)) { elements.set(id, new Element()); }
      return elements.get(id);
    },
    querySelector(selector) { return { value: selections[selector.match(/name="(.*?)"/)[1]] }; },
    createElement(tag) { return new Element(tag); },
  };
  const requests = [];
  const replies = [];
  const fetch = async (url, options) => {
    requests.push({ url, options, body: options.body && JSON.parse(options.body) });
    const reply = replies.shift();
    if (reply instanceof Error) { throw reply; }
    assert.ok(reply, `Unexpected request ${url}`);
    return { ok: reply.status < 400, status: reply.status, json: async () => reply.data };
  };
  let sequence = 0;
  const sandbox = { URL, URLSearchParams };
  vm.runInNewContext(readFileSync(new URL("normalization-review.js", import.meta.url), "utf8"), sandbox);
  const addresses = [];
  const history = { replaceState: (_state, _title, url) => addresses.push(url) };
  const page = new sandbox.NormalizationReviewPage(document, fetch,
    { search },
    { getRandomValues: (bytes) => { bytes.fill(++sequence); return bytes; } }, history);
  page.csrfToken = "session-token";
  page.selected = { label: "Song <script>alert(1)</script>", targetRef: { namespace: "composition", stableKey: "work:456", extra: { revision: 7 } } };
  document.getElementById("reason").value = "Same written work.";
  const reply = (data, status = 200) => replies.push({ status, data });
  return { page, document, selections, requests, replies, reply, addresses };
}

const prepared = { preparationUuid: "saved-preview", targetLabel: "Song <script>alert(1)</script>", warnings: [], affected: { count: 150, items: [{ label: "MSG <img src=x>", detail: "New York" }], complete: false, omittedCount: 149 } };

test("preview preserves opaque source and selected target; untrusted labels remain text", async () => {
  const f = fixture();
  f.reply(prepared);
  await f.page.prepare();
  const request = f.requests[0];
  assert.equal(request.body.sourceRef.stableKey, '["original","song:123"]');
  assert.equal(request.body.sourceRef.ignored, undefined);
  assert.deepEqual(request.body.targetRef, f.page.selected.targetRef);
  assert.equal(request.options.headers["X-CSRF-Token"], "session-token");
  assert.match(request.body.preparationUuid, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  assert.equal(f.document.getElementById("affected-list").children[0].children[0].textContent, "MSG <img src=x>");
  assert.match(f.document.getElementById("preview-coverage").textContent, /150 affected accounts.*149 omitted/);
  assert.match(f.document.getElementById("preview-summary").textContent, /<script>/);
});

test("exact alias scope is explicit, while account-only decisions omit alias restrictions", async () => {
  const f = fixture();
  f.selections.scope = "alias";
  f.document.getElementById("literal").value = "The MSG";
  f.document.getElementById("locality").value = "New York";
  f.document.getElementById("provider").value = "archive.org";
  f.reply(prepared);
  await f.page.prepare();
  assert.equal(f.requests[0].body.literal, "The MSG");
  assert.equal(f.requests[0].body.locality, "New York");
  f.selections.scope = "account";
  f.page.invalidatePreview();
  f.reply(prepared);
  await f.page.prepare();
  assert.equal(f.requests[1].body.locality, undefined);
  assert.equal(f.requests[1].body.literal, undefined);
});

test("lost prepare and apply responses retain retry UUIDs", async () => {
  const f = fixture();
  f.replies.push(new Error("Connection lost"));
  await f.page.prepare();
  f.reply(prepared);
  await f.page.prepare();
  assert.equal(f.requests[0].body.preparationUuid, f.requests[1].body.preparationUuid);
  f.replies.push(new Error("Connection lost"));
  await f.page.apply();
  f.reply({ message: "Saved" });
  f.reply({ source: { label: "Original song" }, history: [] });
  await f.page.apply();
  assert.equal(f.requests[2].body.operationUuid, f.requests[3].body.operationUuid);
  assert.equal(f.requests[3].body.preparationUuid, "saved-preview");
  assert.equal(f.page.preparation, null);
  assert.match(f.document.getElementById("status").textContent, /^Decision saved\./);
});

test("editing invalidates preview; a stale response requires reload before apply", async () => {
  const f = fixture();
  f.reply(prepared);
  await f.page.prepare();
  f.document.getElementById("review-form").listeners.input();
  assert.equal(f.page.preparation, null);
  assert.equal(f.document.getElementById("preview-panel").hidden, true);
  f.reply(prepared);
  await f.page.prepare();
  f.reply({ detail: "stale" }, 409);
  await f.page.apply();
  assert.equal(f.page.preparation, null);
  assert.equal(f.document.getElementById("reload").hidden, false);
  assert.match(f.document.getElementById("status").textContent, /Reload current data/);
  const count = f.requests.length;
  await f.page.apply();
  assert.equal(f.requests.length, count);
});

test("a successful write remains visibly saved when refreshing context fails", async () => {
  const f = fixture();
  f.reply(prepared);
  await f.page.prepare();
  f.reply({});
  f.replies.push(new Error("Refresh unavailable"));
  await f.page.apply();
  assert.match(f.document.getElementById("status").textContent, /^Decision saved\./);
  assert.equal(f.page.preparation, null);
});

test("authentication failures show a recovery instruction and no preview", async () => {
  const f = fixture();
  f.reply({}, 403);
  await f.page.prepare();
  assert.equal(f.page.preparation, null);
  assert.match(f.document.getElementById("status").textContent, /Check Tailscale, then reload/);
  assert.equal(f.document.getElementById("reload").hidden, false);
});

test("a stale CSRF response preserves the server recovery detail and offers reload", async () => {
  const f = fixture();
  f.reply({ detail: "Reload the review page before saving." }, 403);
  await f.page.prepare();
  assert.equal(f.page.preparation, null);
  assert.equal(f.document.getElementById("status").textContent, "Reload the review page before saving.");
  assert.equal(f.document.getElementById("reload").hidden, false);
});

test("withdraw retries retain the operation and expected revision, with history preserved", async () => {
  const f = fixture();
  f.page.currentDecision = { ruleUuid: "rule-1", revision: 3 };
  f.document.getElementById("retract-reason").value = "Different room in the same building.";
  f.replies.push(new Error("Connection lost"));
  await f.page.retract();
  f.reply({});
  f.reply({ source: { label: "MSG" }, currentDecision: { ruleUuid: "rule-1", revision: 4, state: "retracted" } });
  f.reply({ items: [{ revision: 4, action: "retract", reason: "Different room in the same building." }] });
  await f.page.retract();
  assert.equal(f.requests[0].body.operationUuid, f.requests[1].body.operationUuid);
  assert.equal(f.requests[1].body.expectedRevision, 3);
  assert.equal(f.document.getElementById("retract-form").hidden, true);
  assert.match(f.document.getElementById("history").children[0].children[0].textContent, /^Withdrawn/);
});


test("exact alias text preserves surrounding whitespace in the prepared decision", async () => {
  const f = fixture();
  f.selections.scope = "alias";
  f.document.getElementById("literal").value = "  The MSG  ";
  f.reply(prepared);
  await f.page.prepare();
  assert.equal(f.requests[0].body.literal, "  The MSG  ");
  assert.match(f.document.getElementById("preview-summary").textContent, /“  The MSG  ”/);
});


test("people reviews describe name groups and do not claim to change recording credits", async () => {
  const f = fixture("?domain=people&namespace=person_group&stableKey=group-1");
  f.reply({ source: { label: "John Smith", detail: "Shared name group" }, history: [] });
  await f.page.loadContext();
  assert.equal(f.document.getElementById("source-kind").textContent, "Credit name group");
  assert.equal(f.document.getElementById("account-label").textContent, "Only this name group");
  assert.match(f.document.getElementById("scope-help").textContent, /different people across artists/);
  f.reply(prepared);
  await f.page.prepare();
  assert.match(f.document.getElementById("preview-coverage").textContent, /150 affected name groups/);
  f.reply({ ruleUuid: "rule-1", revision: 1, state: "accepted", replayed: false });
  f.reply({ source: { label: "John Smith" }, history: [] });
  f.reply({ items: [] });
  await f.page.apply();
  assert.equal(f.document.getElementById("status").textContent, "Saved for normalization. Individual recording credits have not changed.");
});


test("withdrawn history survives absent current decision and a page reload", async () => {
  const f = fixture("?domain=venues&namespace=legacy_venue&stableKey=venue-1&ruleUuid=rule-saved");
  f.reply({ source: { label: "MSG" }, currentDecision: null, history: [] });
  f.reply({ items: [{ action: "retract", targetLabel: "Madison Square Garden", reason: "Wrong room" }] });
  await f.page.loadContext();
  assert.equal(f.requests[1].url, "/api/reviews/history?ruleUuid=rule-saved");
  assert.equal(f.document.getElementById("history-panel").hidden, false);
  assert.match(f.document.getElementById("history").children[0].children[0].textContent, /^Withdrawn/);
  f.page.rememberRule("new-real-rule");
  assert.match(f.addresses[0], /ruleUuid=new-real-rule/);
});

// Synthetic HTTP fixtures exercise presentation only; the host owns the real benchmark.
const queuePage = {
  total: 21, offset: 0, limit: 10,
  artists: [{ key: "artist-phish", label: "Phish", count: 21 }],
  categories: [{ key: "mixed", label: "Missing and extra titles", count: 21 }],
  limitations: ["Samples are not full setlists."],
  queue: { sha256: "benchmark-sha", rows: 42 },
  items: [
    { id: "sample-1", artist: "Phish", category: "mixed", label: "Disagreement sample", recordedTitlesLabel: "Raw file title samples", recordedTitles: ["Tweezer <script>"], referenceTitlesLabel: "Missing reference song memberships", referenceTitles: ["Reba"], sourceRef: { domain: "songs", namespace: "repertoire", stableKey: '["scope","song:1"]', artistUuid: "artist-phish" }, actionLabel: "Review this song account" },
    { id: "sample-2", artist: "Grateful Dead", category: "unresolved_reference", label: "Unresolved reference", recordedTitlesLabel: "Raw file title samples", recordedTitles: [], referenceTitlesLabel: "Reference song memberships", referenceTitles: [], detail: "Reference identifiers have no resolved title.", sourceRef: null },
  ],
};

test("entry without a source loads benchmark queue with distinct evidence and authenticated actions", async () => {
  const f = fixture("");
  f.reply({ csrfToken: "session-token", reviewer: "Reviewer" });
  f.reply(queuePage);
  await f.page.start();
  assert.equal(f.requests[1].url, "/api/reviews/queue?domain=songs&offset=0&limit=10");
  assert.equal(f.document.getElementById("queue-panel").hidden, false);
  assert.equal(f.document.getElementById("queue-next").disabled, false);
  assert.match(f.document.getElementById("queue-totals").textContent, /^21 benchmark disagreements/);
  const [linked, unresolved] = f.document.getElementById("queue-items").children;
  assert.match(linked.children[2].textContent, /Raw file title/);
  assert.equal(linked.children[3].children[0].textContent, "Tweezer <script>");
  assert.match(linked.children.at(-1).href, /stableKey=%5B%22scope%22%2C%22song%3A1%22%5D/);
  assert.equal(unresolved.children.some((node) => node.href), false);
  assert.match(unresolved.children.at(-1).textContent, /No authenticated song account/);
});

test("queue sends artist/category filters and page offset to the host", async () => {
  const f = fixture("");
  f.page.queueArtist = "artist-phish";
  f.page.queueCategory = "mixed";
  f.reply({ ...queuePage, offset: 20, items: queuePage.items.slice(0, 1) });
  await f.page.loadQueue(20);
  const query = new URLSearchParams(f.requests[0].url.split("?")[1]);
  assert.equal(query.get("artist"), "artist-phish");
  assert.equal(query.get("category"), "mixed");
  assert.equal(query.get("offset"), "20");
  assert.equal(f.document.getElementById("queue-next").disabled, true);
});

test("conflicting applicable decisions do not imply a winning identity; explicit focus retains history", async () => {
  const f = fixture("?domain=venues&namespace=legacy_venue&stableKey=venue-1");
  const decisions = [{ ruleUuid: "rule-a", action: "accept", targetLabel: "Room A" }, { ruleUuid: "rule-b", action: "accept", targetLabel: "Room B" }];
  f.reply({ source: { label: "MSG" }, currentDecision: null, applicableDecisions: decisions, warnings: ["Conflicting accepted targets."], history: [] });
  await f.page.loadContext();
  assert.equal(f.document.getElementById("current-panel").hidden, true);
  assert.equal(f.document.getElementById("applicable-decisions").children.length, 2);
  assert.match(f.document.getElementById("decision-warnings").children[0].textContent, /Conflicting/);
  f.page.rememberRule("rule-a");
  f.reply({ source: { label: "MSG" }, currentDecision: null, selectedDecision: { ...decisions[0], state: "retracted", revision: 2 }, applicableDecisions: decisions, warnings: [], history: [] });
  f.reply({ items: [{ action: "retract", targetLabel: "Room A" }] });
  await f.page.loadContext();
  assert.match(f.requests[1].url, /ruleUuid=rule-a/);
  assert.equal(f.document.getElementById("decision-heading").textContent, "Selected decision");
  assert.equal(f.document.getElementById("retract-form").hidden, true);
  assert.match(f.document.getElementById("history").children[0].children[0].textContent, /^Withdrawn/);
});


test("songs mode searches within exact artist and captured scope, then prepares opaque references", async () => {
  const artistUuid = "57fbdb7e-834d-4a79-a36c-229717980bd4";
  const sourceKey = JSON.stringify(["captured:phish", "song:raw-title"]);
  const search = `?${new URLSearchParams({ domain: "songs", namespace: "repertoire", stableKey: sourceKey, artistUuid })}`;
  const f = fixture(search);
  f.reply({ csrfToken: "session-token", reviewer: "Reviewer" });
  f.reply({ source: { label: "Tweezer Reprise", artistUuid }, history: [] });
  await f.page.start();
  assert.equal(f.document.getElementById("target-heading").textContent, "Resolve this song");
  assert.equal(f.document.getElementById("alias-choice").hidden, false);
  assert.equal(f.document.getElementById("scope-alias").disabled, false);
  f.document.getElementById("candidate-query").value = "Tweezer";
  const targetRef = { domain: "songs", namespace: "repertoire", stableKey: JSON.stringify(["captured:phish", "song:target"]), artistUuid };
  f.reply({ items: [{ label: "Tweezer Reprise", targetRef }, { label: "Tweezer Reprise", targetRef: { domain: "songs", namespace: "repertoire", stableKey: sourceKey, artistUuid } }], complete: true });
  await f.page.search();
  const query = new URLSearchParams(f.requests[2].url.split("?")[1]);
  assert.equal(query.get("domain"), "songs");
  assert.equal(query.get("artistUuid"), artistUuid);
  assert.equal(query.get("scope"), "captured:phish");
  const [targetButton, currentButton] = f.document.getElementById("candidates").children.map(row => row.children[0]);
  assert.equal(currentButton.disabled, true);
  assert.equal(currentButton.children[0].textContent, "Tweezer Reprise (this song entry)");
  assert.equal(targetButton.disabled, false);
  targetButton.listeners.click();
  f.document.getElementById("review-form").querySelectorAll = () => [targetButton, currentButton];
  f.reply(prepared);
  await f.page.prepare();
  assert.equal(currentButton.disabled, true);
  assert.equal(targetButton.disabled, false);
  assert.equal(f.requests[3].body.sourceRef.stableKey, sourceKey);
  assert.deepEqual(f.requests[3].body.targetRef, targetRef);
  assert.equal(f.requests[3].body.scope, "account");
  assert.match(f.document.getElementById("preview-summary").textContent, /same song as/);
  f.reply({ ruleUuid: "rule-song", revision: 1, state: "accepted" });
  f.reply({ source: { label: "Tweezer Reprise", artistUuid }, history: [] });
  f.reply({ items: [] });
  await f.page.apply();
  assert.equal(f.document.getElementById("status").textContent, "Decision saved. Reload the song page to see the correction.");
});

test("song alias review preserves its exact spelling and fixes the artist scope", async () => {
  const artistUuid = "77a58ff9-2e01-c59c-b8eb-cff106049b72";
  const sourceKey = JSON.stringify(["catalogue", "repertoire:variant"]);
  const f = fixture(`?${new URLSearchParams({ domain: "songs", namespace: "repertoire", stableKey: sourceKey, artistUuid })}`);
  f.reply({ csrfToken: "session-token", reviewer: "Reviewer" });
  f.reply({ source: { label: "Playin' in the Band", literal: "Playin' in the Band", artistUuid }, history: [] });
  await f.page.start();
  assert.equal(f.document.getElementById("artist-uuid").readOnly, true);
  f.selections.scope = "alias";
  const targetRef = { domain: "songs", namespace: "repertoire", stableKey: JSON.stringify(["catalogue", "repertoire:imported"]), artistUuid };
  f.page.selected = { label: "Playing in the Band", targetRef };
  f.reply(prepared);
  await f.page.prepare();
  assert.equal(f.requests[2].body.scope, "alias");
  assert.equal(f.requests[2].body.literal, "Playin' in the Band");
  assert.equal(f.requests[2].body.artistUuid, artistUuid);
  assert.deepEqual(f.requests[2].body.targetRef, targetRef);
  assert.match(f.document.getElementById("preview-summary").textContent, /Reuse the exact spelling/);
});

test("revising a saved alias restores its selector instead of creating an account rule", async () => {
  const f = fixture();
  const selector = { literal: "The MSG", artistUuid: "artist-one", provider: "archive.org", locality: "New York", from: "1980-01-01", through: "1990-12-31" };
  f.page.currentDecision = { scope: "alias", selector };
  f.document.getElementById("literal").value = "Unrelated current spelling";
  f.page.revise();
  assert.equal(f.document.getElementById("scope-account").checked, false);
  assert.equal(f.document.getElementById("scope-alias").checked, true);
  assert.equal(f.document.getElementById("alias-fields").hidden, false);
  f.selections.scope = "alias";
  f.reply(prepared);
  await f.page.prepare();
  const request = f.requests[0].body;
  assert.equal(request.scope, "alias");
  for (const [field, value] of Object.entries(selector)) { assert.equal(request[field], value); }
});

test("invalid songs scope or missing artist stops candidate search with a usable instruction", async () => {
  for (const stableKey of ["invalid-json", '["scope","song"]']) {
    const f = fixture(`?${new URLSearchParams({ domain: "songs", namespace: "repertoire", stableKey })}`);
    f.document.getElementById("candidate-query").value = "Tweezer";
    await f.page.search();
    assert.equal(f.requests.length, 0);
    assert.match(f.document.getElementById("candidate-status").textContent, /Open Review again from the song account/);
  }
});

test("source and candidate evidence links open original reader evidence separately from selection", async () => {
  const f = fixture("?domain=venues&namespace=legacy_venue&stableKey=original-venue");
  const origin = "http://100.73.204.24:18774";
  const sourceUrl = origin + "/api/evidence?id=original-venue";
  const targetUrl = origin + "/api/evidence?id=target-venue";
  f.reply({ source: { label: "MSG", evidenceUrl: sourceUrl }, evidenceOrigin: origin, history: [] });
  await f.page.loadContext();
  const sourceLink = f.document.getElementById("source-evidence").children[0];
  assert.equal(sourceLink.textContent, "Inspect original evidence");
  assert.equal(sourceLink.href, sourceUrl);
  assert.equal(sourceLink.target, "_blank");
  assert.equal(sourceLink.rel, "noopener noreferrer");

  f.document.getElementById("candidate-query").value = "Madison";
  const targetRef = { domain: "venues", namespace: "legacy_venue", stableKey: "target-venue" };
  f.reply({ items: [{ label: "Madison Square Garden", targetRef, evidenceUrl: targetUrl }], evidenceOrigin: origin });
  f.page.selected = null;
  await f.page.search();
  const row = f.document.getElementById("candidates").children[0];
  const [button, evidence] = row.children;
  assert.equal(button.tagName, "BUTTON");
  assert.equal(evidence.tagName, "A");
  assert.equal(evidence.textContent, "Inspect evidence");
  assert.equal(evidence.href, targetUrl);
  assert.equal(button.children.some(child => child.tagName === "A"), false);
  assert.equal(f.page.selected, null);
  button.listeners.click();
  assert.deepEqual(f.page.selected.targetRef, targetRef);
  assert.equal(button.getAttribute("aria-pressed"), "true");
  assert.equal(f.requests.length, 2, "evidence links navigate without fetching or saving through the review page");
});

test("external, credential-bearing, malformed and executable evidence URLs are omitted", async () => {
  const origin = "http://100.73.204.24:18774";
  for (const evidenceUrl of [
    "javascript:alert(1)", "data:text/html,unsafe", "https://example.org/evidence",
    "http://100.73.204.24:19999/api/evidence", "http://user:password@100.73.204.24:18774/api/evidence",
    "//example.org/evidence", "/api/evidence?id=relative", "not a URL",
  ]) {
    const f = fixture("?domain=venues&namespace=legacy_venue&stableKey=original-venue");
    f.reply({ source: { label: "Original venue", evidenceUrl }, evidenceOrigin: origin, history: [] });
    await f.page.loadContext();
    assert.equal(f.document.getElementById("source-evidence").children.length, 0, evidenceUrl);
    f.document.getElementById("candidate-query").value = "Venue";
    f.reply({ items: [{ label: "Candidate", targetRef: { domain: "venues", namespace: "legacy_venue", stableKey: "candidate" }, evidenceUrl }], evidenceOrigin: origin });
    await f.page.search();
    const row = f.document.getElementById("candidates").children[0];
    assert.equal(row.children.length, 1, evidenceUrl);
    assert.equal(row.children[0].tagName, "BUTTON");
  }
});
