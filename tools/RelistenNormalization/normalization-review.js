/* Shared operator page. The server owns identities, evidence, scope expansion and review
 * concurrency. This page keeps opaque target references intact and confirms a saved preview. */
(function (root) {
  "use strict";

  class ReviewPage {
    constructor(document, fetch, location, crypto, history = root.history) {
      this.document = document;
      this.fetch = fetch;
      this.crypto = crypto;
      this.history = history;
      this.query = new URLSearchParams(location.search);
      this.sourceRef = Object.fromEntries(
        ["domain", "namespace", "stableKey", "artistUuid"]
          .filter((key) => this.query.has(key))
          .map((key) => [key, this.query.get(key)]),
      );
      this.sourceRef.stableKey ||= this.query.get("key");
      if (!this.sourceRef.artistUuid && this.query.has("artist")) {
        this.sourceRef.artistUuid = this.query.get("artist");
      }
      this.knownRuleUuid = this.query.get("ruleUuid");
      this.queueArtist = this.query.get("queueArtist") || this.query.get("artist") || "";
      this.queueCategory = this.query.get("category") || "";
      this.queuePage = null;
      this.selected = null;
      this.preparation = null;
      this.prepareAttempt = null;
      this.retractAttempt = null;
      this.busy = false;
      this.searchVersion = 0;
      this.bindEvents();
    }

    newUuid() {
      // Tailscale readers use HTTP. getRandomValues also works outside a secure context,
      // where the browser may not expose crypto.randomUUID.
      const bytes = this.crypto.getRandomValues(new Uint8Array(16));
      bytes[6] = (bytes[6] & 15) | 64;
      bytes[8] = (bytes[8] & 63) | 128;
      const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
      return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
    }

    element(id) { return this.document.getElementById(id); }
    text(id, value) { this.element(id).textContent = value || ""; }
    value(id) { return this.element(id).value.trim(); }
    checked(name) { return this.document.querySelector(`input[name="${name}"]:checked`).value; }
    show(id, visible) { this.element(id).hidden = !visible; }
    status(message, error = false) {
      this.text("status", message);
      this.element("status").classList.toggle("error", error);
    }

    bindEvents() {
      this.element("queue-filters").addEventListener("submit", (event) => {
        event.preventDefault();
        this.queueArtist = this.element("queue-artist").value;
        this.queueCategory = this.element("queue-category").value;
        this.run(() => this.loadQueue(0));
      });
      this.element("queue-previous").addEventListener("click", () => {
        if (this.queuePage) { this.run(() => this.loadQueue(Math.max(0, this.queuePage.offset - this.queuePage.limit))); }
      });
      this.element("queue-next").addEventListener("click", () => {
        if (this.queuePage) { this.run(() => this.loadQueue(this.queuePage.offset + this.queuePage.limit)); }
      });
      this.element("review-form").addEventListener("submit", (event) => {
        event.preventDefault();
        this.prepare();
      });
      this.element("review-form").addEventListener("input", () => {
        this.invalidatePreview();
        this.updateScopeSummary();
      });
      this.element("review-form").addEventListener("change", () => {
        this.invalidatePreview();
        this.show("alias-fields", this.checked("scope") === "alias");
      });
      this.element("search").addEventListener("click", () => this.search());
      this.element("candidate-query").addEventListener("keydown", (event) => {
        if (event.key === "Enter") {
          event.preventDefault();
          this.search();
        }
      });
      this.element("apply").addEventListener("click", () => this.apply());
      this.element("edit").addEventListener("click", () => this.invalidatePreview());
      this.element("reload").addEventListener("click", () => this.start());
      this.element("revise").addEventListener("click", () => {
        this.invalidatePreview();
        this.status("Choose the replacement below. Saving keeps the earlier decision in history.");
        this.element("review-form").scrollIntoView({ behavior: "smooth", block: "start" });
        this.element("candidate-query").focus();
      });
      this.element("retract-form").addEventListener("submit", (event) => {
        event.preventDefault();
        this.retract();
      });
      this.element("retract-reason").addEventListener("input", () => { this.retractAttempt = null; });
    }

    async request(path, body) {
      const options = { credentials: "same-origin", headers: { Accept: "application/json" } };
      if (body) {
        options.method = "POST";
        options.headers["Content-Type"] = "application/json";
        options.headers["X-CSRF-Token"] = this.csrfToken;
        options.body = JSON.stringify(body);
      }
      const response = await this.fetch(path, options);
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        let message = data.detail || data.message || data.error || data.title || `Request failed (${response.status}).`;
        if (response.status === 401 || response.status === 403) {
          message = data.detail || data.message || data.error || data.title
            || "Operator access could not be verified. Check Tailscale, then reload this page.";
          this.show("reload", true);
        } else if (response.status === 409) {
          message = "The account or decision changed. Reload current data, then preview your decision again.";
          this.invalidatePreview();
          this.show("reload", true);
        }
        throw new Error(message);
      }
      return data;
    }

    async run(action) {
      if (this.busy) { return; }
      this.busy = true;
      this.updateButtons();
      try { await action(); }
      catch (error) { this.status(error.message || "The request failed. You can retry.", true); }
      finally {
        this.busy = false;
        this.updateButtons();
      }
    }

    updateButtons() {
      for (const control of this.element("review-form").querySelectorAll("input,textarea,button")) {
        control.disabled = this.busy || control.getAttribute("data-current-song") === "true";
      }
      this.element("retract-reason").disabled = this.busy;
      this.element("prepare").disabled = this.busy || !this.selected;
      this.element("apply").disabled = this.busy || !this.preparation;
      this.element("retract").disabled = this.busy;
      this.element("search").disabled = this.busy;
      this.element("scope-alias").disabled = this.busy || this.sourceRef.domain === "songs";
      for (const id of ["queue-filter", "queue-artist", "queue-category"]) { this.element(id).disabled = this.busy; }
      this.element("queue-previous").disabled = this.busy || !this.queuePage || this.queuePage.offset === 0;
      this.element("queue-next").disabled = this.busy || !this.queuePage || this.queuePage.offset + this.queuePage.limit >= this.queuePage.total;
    }

    invalidatePreview() {
      this.preparation = null;
      this.prepareAttempt = null;
      this.show("preview-panel", false);
      this.updateButtons();
    }

    async start() {
      await this.run(async () => {
        const session = await this.request("/api/session");
        if (!session.csrfToken) {
          this.show("reload", true);
          throw new Error("Operator session could not be verified. Check Tailscale, then reload this page.");
        }
        this.csrfToken = session.csrfToken;
        this.text("reviewer", session.reviewer);
        if (!this.sourceRef.stableKey && !this.sourceRef.namespace) {
          await this.loadQueue(0);
          this.show("reload", false);
          return;
        }
        if (!["venues", "compositions", "people", "songs"].includes(this.sourceRef.domain)
          || !["legacy_venue", "repertoire", "person_group"].includes(this.sourceRef.namespace)
          || !this.sourceRef.stableKey) {
          throw new Error("Open Review from a venue, song or person account to choose what to correct.");
        }
        if (this.sourceRef.domain === "songs") { this.songScope(); }
        this.show("queue-panel", false);
        this.invalidatePreview();
        this.selected = null;
        this.show("selected-target", false);
        this.element("candidates").replaceChildren();
        await this.loadContext();
        this.show("reload", false);
        this.status(this.sourceRef.domain === "songs"
          ? "Choose an existing song by the same artist, then preview the same-song relationship."
          : this.sourceRef.domain === "people"
          ? "Choose a name group, then preview the groups your decision will affect."
          : "Choose an identity, then preview the accounts your decision will affect.");
      });
    }

    async loadQueue(offset) {
      this.status("Loading benchmark disagreements…");
      const params = new URLSearchParams({ domain: "songs", offset, limit: 10 });
      if (this.queueArtist) { params.set("artist", this.queueArtist); }
      if (this.queueCategory) { params.set("category", this.queueCategory); }
      const page = await this.request(`/api/reviews/queue?${params}`);
      this.queuePage = page;
      this.renderQueueOptions("queue-artist", "All artists", page.artists, this.queueArtist);
      this.renderQueueOptions("queue-category", "All categories", page.categories, this.queueCategory);
      this.text("queue-totals", `${page.total} benchmark disagreement${page.total === 1 ? "" : "s"} in this selection.`);
      this.element("queue-counts").replaceChildren();
      for (const category of page.categories) {
        this.element("queue-counts").append(this.node("span", `${category.label}: ${category.count}`));
      }
      this.element("queue-limitations").replaceChildren();
      for (const limitation of page.limitations) { this.element("queue-limitations").append(this.node("li", limitation)); }
      this.text("queue-snapshot", `${page.queue.rows} retained rows · SHA-256 ${page.queue.sha256}`);
      this.element("queue-items").replaceChildren();
      for (const item of page.items) { this.element("queue-items").append(this.queueItem(item)); }
      const first = page.items.length ? page.offset + 1 : 0;
      this.text("queue-page", `${first}–${page.offset + page.items.length} of ${page.total}`);
      this.show("queue-panel", true);
      this.status(page.items.length ? "Compare the original evidence before choosing a correction." : "No disagreements in this selection.");
      this.updateButtons();
    }

    renderQueueOptions(id, label, items, selected) {
      const control = this.element(id);
      control.replaceChildren();
      const all = this.node("option", label);
      all.value = "";
      control.append(all);
      for (const item of items) {
        const option = this.node("option", `${item.label} (${item.count})`);
        option.value = item.key;
        control.append(option);
      }
      control.value = selected;
    }

    queueItem(item) {
      const card = this.node("article", undefined, "panel queue-record");
      card.append(this.node("h2", item.label));
      card.append(this.node("p", [item.artist, item.date, item.category].filter(Boolean).join(" · "), "muted"));
      if (item.detail) { card.append(this.node("p", item.detail)); }
      for (const [label, titles] of [[item.recordedTitlesLabel, item.recordedTitles], [item.referenceTitlesLabel, item.referenceTitles]]) {
        card.append(this.node("h3", label));
        const list = this.node("ul");
        for (const title of titles) { list.append(this.node("li", title)); }
        if (!titles.length) { list.append(this.node("li", "No titles supplied for this sample.")); }
        card.append(list);
      }
      if (item.sourceRef) {
        // Only the host can authenticate the benchmark item against a current reader account.
        const link = this.node("a", item.actionLabel || "Review the linked song/composition account");
        link.href = `?${new URLSearchParams(item.sourceRef)}`;
        card.append(link);
      } else {
        card.append(this.node("p", "No authenticated song account is linked to this item. Inspect its evidence before choosing a correction.", "muted"));
      }
      return card;
    }

    updateScopeSummary() {
      const parts = [`Match the exact name “${this.element("literal").value}”.`];
      if (this.value("locality")) { parts.push(`Locality: ${this.value("locality")}.`); }
      if (this.value("provider")) { parts.push(`Provider: ${this.value("provider")}.`); }
      parts.push(this.value("artist-uuid") ? "Limited to the selected artist." : "Across artists.");
      if (this.value("from")) { parts.push(`From ${this.value("from")}.`); }
      if (this.value("through")) { parts.push(`Through ${this.value("through")}.`); }
      this.text("alias-scope-summary", parts.join(" "));
    }

    configureDomainCopy() {
      this.text("source-kind", { venues: "Imported venue", compositions: "Artist song", people: "Credit name group", songs: "Artist song" }[this.sourceRef.domain]);
      if (this.sourceRef.domain === "songs") {
        this.text("target-heading", "Resolve this song");
        this.text("candidate-label", "Choose an existing song by the same artist");
        this.text("preview-title", "Review this same-song relationship");
        this.element("reason").placeholder = "Explain why these titles are the same song, or why they differ.";
        this.text("accept-label", "These are the same song");
        this.text("reject-label", "These are different songs");
        this.text("account-label", "Only this artist song");
        this.text("scope-help", "This saves a same-song relationship within this artist and captured catalog scope. Individual files and setlists stay unchanged.");
        this.show("alias-choice", false);
        this.show("alias-fields", false);
        this.element("scope-account").checked = true;
        this.element("scope-alias").disabled = true;
        return;
      }
      if (this.sourceRef.domain !== "people") { return; }
      this.text("target-heading", "Choose the matching name group");
      this.text("preview-title", "Review the affected name groups");
      this.text("candidate-label", "Find a name group");
      this.text("accept-label", "Use this name group");
      this.text("reject-label", "Reject this name group");
      this.text("account-label", "Only this name group");
      this.text("scope-help", "A name group collects shared wording and can include different people across artists. This rule saves a name-group relationship for normalization; individual recording credits stay unchanged.");
      this.text("retract-help", "The rule stays in history. Withdrawing it does not change individual recording credits.");
    }

    rememberRule(ruleUuid) {
      if (!ruleUuid) { return; }
      this.knownRuleUuid = ruleUuid;
      this.query.set("ruleUuid", ruleUuid);
      this.history?.replaceState(null, "", `?${this.query}`);
    }

    async loadContext() {
      this.configureDomainCopy();
      const params = new URLSearchParams(this.sourceRef);
      if (this.knownRuleUuid) { params.set("ruleUuid", this.knownRuleUuid); }
      const context = await this.request(`/api/reviews/context?${params}`);
      this.context = context;
      const source = context.source || {};
      this.text("source-label", source.label || this.sourceRef.stableKey);
      this.text("source-detail", source.detail);
      this.text("source-reference", JSON.stringify(this.sourceRef, null, 2));
      this.element("literal").value = source.literal || "";
      this.element("artist-uuid").value = source.artistUuid || this.sourceRef.artistUuid || "";
      const fixedArtist = this.sourceRef.domain === "compositions" || this.sourceRef.domain === "songs";
      this.element("artist-uuid").readOnly = fixedArtist;
      this.text("artist-scope-hint", fixedArtist
        ? "Song aliases stay within this artist; the artist scope is fixed."
        : "Clear Artist UUID to match across artists.");
      this.element("provider").value = source.provider || "";
      this.element("locality").value = source.locality || "";
      this.updateScopeSummary();
      this.show("source-panel", true);
      this.show("review-form", true);
      this.element("decision-warnings").replaceChildren();
      for (const warning of context.warnings || []) { this.element("decision-warnings").append(this.node("p", warning, "error")); }
      const applicable = context.applicableDecisions || [];
      this.element("applicable-decisions").replaceChildren();
      if (applicable.length > 1) {
        for (const decision of applicable) {
          const button = this.node("button", `View ${decision.action === "reject" ? "rejection" : "acceptance"}: ${decision.targetLabel}`);
          button.type = "button";
          button.addEventListener("click", () => this.run(async () => {
            this.rememberRule(decision.ruleUuid);
            await this.loadContext();
            this.status("Viewing the selected decision and its history. This does not establish a winning identity.");
          }));
          this.element("applicable-decisions").append(button);
        }
      }
      this.show("decision-context-panel", applicable.length > 1 || (context.warnings || []).length > 0);
      this.currentDecision = context.selectedDecision || context.currentDecision || null;
      this.text("decision-heading", context.selectedDecision ? "Selected decision" : "Current decision");
      this.show("current-panel", !!this.currentDecision);
      this.show("retract-form", !!this.currentDecision && this.currentDecision.state !== "retracted");
      this.element("current-decision").replaceChildren();
      if (this.currentDecision) {
        this.element("current-decision").append(this.decisionNode(this.currentDecision));
      }
      let history = context.history || [];
      this.rememberRule(this.currentDecision?.ruleUuid);
      if (this.knownRuleUuid) {
        const result = await this.request(`/api/reviews/history?${new URLSearchParams({ ruleUuid: this.knownRuleUuid })}`);
        history = result.items;
      }
      this.renderHistory(history);
    }

    node(tag, text, className) {
      const element = this.document.createElement(tag);
      if (text !== undefined) { element.textContent = String(text); }
      if (className) { element.className = className; }
      return element;
    }

    decisionNode(decision) {
      const item = this.node("div", undefined, "history-item");
      const action = (decision.state === "retracted" || decision.action === "retract") ? "Withdrawn" : decision.action === "reject" ? "Rejected" : "Accepted";
      item.append(this.node("strong", `${action}: ${decision.targetLabel || "identity"}`));
      item.append(this.node("p", decision.scope === "alias" ? "Reusable exact-name decision" : this.sourceRef.domain === "people" ? "This name group only" : "This account only"));
      if (decision.reason) { item.append(this.node("p", decision.reason)); }
      const byline = [decision.reviewer, decision.createdAt].filter(Boolean).join(" · ");
      if (byline) { item.append(this.node("p", byline)); }
      return item;
    }

    renderHistory(history) {
      this.element("history").replaceChildren();
      for (const decision of history) { this.element("history").append(this.decisionNode(decision)); }
      this.show("history-panel", history.length > 0);
    }

    songScope() {
      let key;
      try { key = JSON.parse(this.sourceRef.stableKey); }
      catch (_) { throw new Error("This song link has an invalid catalog reference. Open Review again from the song account."); }
      if (this.sourceRef.namespace !== "repertoire" || !Array.isArray(key) || key.length !== 2
        || key.some((part) => typeof part !== "string" || !part.length)
        || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(this.sourceRef.artistUuid || "")) {
        throw new Error("This song link needs its artist and catalog scope. Open Review again from the song account.");
      }
      return key[0];
    }

    async search() {
      const query = this.value("candidate-query");
      if (!query) { this.text("candidate-status", "Enter a name to search."); return; }
      const version = ++this.searchVersion;
      this.text("candidate-status", "Searching…");
      try {
        const params = new URLSearchParams({ q: query, domain: this.sourceRef.domain });
        if (this.sourceRef.domain === "songs") {
          params.set("scope", this.songScope());
          params.set("artistUuid", this.sourceRef.artistUuid);
        }
        const result = await this.request(`/api/reviews/candidates?${params}`);
        if (version !== this.searchVersion) { return; }
        const candidates = result.items;
        this.element("candidates").replaceChildren();
        for (const candidate of candidates) {
          const button = this.node("button", undefined, "candidate");
          button.type = "button";
          button.setAttribute("aria-pressed", "false");
          const isCurrentSong = this.sourceRef.domain === "songs"
            && candidate.targetRef.namespace === this.sourceRef.namespace
            && candidate.targetRef.stableKey === this.sourceRef.stableKey
            && candidate.targetRef.artistUuid === this.sourceRef.artistUuid;
          button.disabled = isCurrentSong;
          button.setAttribute("data-current-song", String(isCurrentSong));
          button.append(this.node("strong", `${candidate.label}${isCurrentSong ? " (this song entry)" : ""}`));
          if (candidate.detail) { button.append(this.node("span", candidate.detail)); }
          button.addEventListener("click", () => {
            if (!isCurrentSong) { this.selectTarget(candidate, button); }
          });
          this.element("candidates").append(button);
        }
        const omitted = result.omittedCount || 0;
        this.text("candidate-status", candidates.length
          ? `${candidates.length} ${this.sourceRef.domain === "people" ? "name groups" : this.sourceRef.domain === "songs" ? "artist songs" : "identities"} shown.${result.complete === false ? ` More results are available${omitted ? ` (${omitted} omitted)` : ""}; refine your search.` : ""}`
          : this.sourceRef.domain === "people" ? "No name groups found. Try another name." : "No identities found. Try another name.");
      } catch (error) {
        if (version === this.searchVersion) { this.text("candidate-status", error.message); }
      }
    }

    selectTarget(candidate, button) {
      if (this.busy) { return; }
      // Keep the server's target reference intact. Labels and reader group keys are not identities.
      this.selected = candidate;
      this.invalidatePreview();
      for (const item of this.element("candidates").children) { item.setAttribute("aria-pressed", String(item === button)); }
      this.text("selected-target", `Selected: ${candidate.label}${candidate.detail ? ` · ${candidate.detail}` : ""}`);
      this.show("selected-target", true);
    }

    decisionRequest() {
      const request = {
        sourceRef: this.sourceRef,
        targetRef: this.selected.targetRef,
        action: this.checked("action"),
        scope: this.sourceRef.domain === "songs" ? "account" : this.checked("scope"),
        reason: this.value("reason"),
      };
      if (request.scope === "alias") {
        request.literal = this.element("literal").value;
        for (const [field, id] of [["artistUuid", "artist-uuid"], ["provider", "provider"], ["locality", "locality"], ["from", "from"], ["through", "through"]]) {
          if (this.value(id)) { request[field] = this.value(id); }
        }
      }
      return request;
    }

    async prepare() {
      if (!this.selected || !this.element("review-form").reportValidity()) { return; }
      if (this.checked("scope") === "alias" && !this.value("literal")) {
        this.status("Enter the exact original name for a reusable decision.", true);
        this.element("literal").focus();
        return;
      }
      await this.run(async () => {
        const request = this.decisionRequest();
        const fingerprint = JSON.stringify(request);
        if (this.prepareAttempt?.fingerprint !== fingerprint) {
          this.prepareAttempt = { fingerprint, uuid: this.newUuid() };
        }
        this.status(this.sourceRef.domain === "people" ? "Preparing the affected name groups…" : "Preparing the exact affected accounts…");
        const preview = await this.request("/api/reviews/prepare", { ...request, preparationUuid: this.prepareAttempt.uuid });
        this.preparation = { preview, operationUuid: this.newUuid() };
        this.renderPreview(preview, request);
        this.status(this.sourceRef.domain === "people" ? "Check the affected name groups before saving." : "Check the affected accounts before saving.");
      });
    }

    renderPreview(preview, request) {
      const affected = preview.affected;
      this.text("preview-summary", `${request.action === "reject" ? "Reject" : "Use"} ${preview.targetLabel}. ${request.scope === "alias" ? `Reuse the exact name “${request.literal}” within the selected scope.` : this.sourceRef.domain === "people" ? "Apply only to this name group." : "Apply only to this account."}`);
      if (this.sourceRef.domain === "songs") {
        this.text("preview-summary", `${request.action === "reject" ? "Reject the same-song relationship with" : "Treat this artist song as the same song as"} ${preview.targetLabel}. The artist and captured catalog scope stay fixed.`);
      }
      const items = affected.items;
      const count = affected.count;
      this.text("preview-coverage", `${count} affected ${this.sourceRef.domain === "people" ? "name group" : "account"}${count === 1 ? "" : "s"}. ${affected.complete === true ? "The list below is complete." : `Showing ${items.length}; ${affected.omittedCount} omitted. The decision applies to the full prepared scope.`}`);
      this.element("affected-list").replaceChildren();
      for (const account of items) {
        const item = this.node("li");
        item.append(this.node("strong", account.label));
        if (account.detail) { item.append(this.node("span", account.detail)); }
        this.element("affected-list").append(item);
      }
      this.element("preview-warnings").replaceChildren();
      for (const warning of preview.warnings) { this.element("preview-warnings").append(this.node("p", warning)); }
      this.show("preview-panel", true);
      this.element("preview-panel").scrollIntoView({ behavior: "smooth", block: "start" });
    }

    async apply() {
      if (!this.preparation) { return; }
      await this.run(async () => {
        // Retain the UUID after a lost response; retrying cannot create a second decision.
        const pending = this.preparation;
        this.status("Saving decision…");
        const result = await this.request("/api/reviews/apply", { operationUuid: pending.operationUuid, preparationUuid: pending.preview.preparationUuid });
        this.rememberRule(result.ruleUuid);
        this.invalidatePreview();
        try {
          await this.loadContext();
          this.status(this.sourceRef.domain === "songs"
            ? "Saved for matching. Individual files and setlists have not changed."
            : this.sourceRef.domain === "people"
            ? "Saved for normalization. Individual recording credits have not changed."
            : "Decision saved. Reader results may need a refresh before they show the correction.");
        } catch (error) {
          this.show("reload", true);
          this.status(`${this.sourceRef.domain === "songs" ? "Saved for matching. Individual files and setlists have not changed." : this.sourceRef.domain === "people" ? "Saved for normalization. Individual recording credits have not changed." : "Decision saved."} Could not refresh the current decision: ${error.message}`, true);
        }
      });
    }

    async retract() {
      if (!this.currentDecision || !this.element("retract-form").reportValidity()) { return; }
      await this.run(async () => {
        const request = { ruleUuid: this.currentDecision.ruleUuid, expectedRevision: this.currentDecision.revision, reason: this.value("retract-reason") };
        const fingerprint = JSON.stringify(request);
        if (this.retractAttempt?.fingerprint !== fingerprint) {
          this.retractAttempt = { fingerprint, uuid: this.newUuid() };
        }
        await this.request("/api/reviews/retract", { ...request, operationUuid: this.retractAttempt.uuid });
        this.invalidatePreview();
        try {
          await this.loadContext();
          this.status("Decision withdrawn. The original decision remains in history.");
        } catch (error) {
          this.show("reload", true);
          this.status(`Decision withdrawn. Could not refresh history: ${error.message}`, true);
        }
      });
    }
  }

  root.NormalizationReviewPage = ReviewPage;
  if (root.document) {
    const page = new ReviewPage(root.document, root.fetch.bind(root), root.location, root.crypto);
    page.start();
  }
})(globalThis);
