// Fetch resolves for HTTP errors; every admin action checks status before reporting success.
async function readAdminResponse(response) {
  if (response.redirected) {
    throw new Error("Your session may have expired. Reload the admin and sign in again.");
  }
  var text = await response.text();
  var message = text;
  if ((response.headers.get("Content-Type") || "").includes("json")) {
    var body = JSON.parse(text);
    message = body.message || body.title || text;
  }
  if (!response.ok) {
    throw new Error(message || "Request failed (HTTP " + response.status + ").");
  }
  return message;
}

var addArtist = new Vue({
  el: "#add-artist",
  data: () => {
    var features = Object.keys(window.artists[0].features)
      .filter((f) => f != "id" && f != "artist_id");

    var sel = {};

    features.forEach((feature) => {
      sel[feature] = false;
    });

    return {
      artists: window.artists,
      upstream_sources: window.upstream_sources,
      features: features,

      selected: {
        id: -1,
        features: sel,
        name: "",
        slug: "",
        musicbrainz_id: "",
        featured: "0",
        upstream_sources: []
      },

      loading: false,
      result: null
    }
  },
  methods: {
    artistForId: function (id) {
      return this.$data.artists.filter(function (a) {
        return a.id === parseInt(id, 10);
      })[0];
    },
    copyFromExistingArtist: function (e) {
      var val = e.target.value;
      var artist = this.artistForId(val);

      this.$data.features.forEach((feature) => {
        this.$data.selected.features[feature] = artist.features[feature];
      });

      this.$data.selected.id = artist.id;
      this.$data.selected.name = artist.name;
      this.$data.selected.slug = artist.slug;
      this.$data.selected.musicbrainz_id = artist.musicbrainz_id;
      this.$data.selected.featured = "" + artist.featured;
      this.$data.selected.upstream_sources = JSON.parse(JSON.stringify(artist.upstream_sources));

      console.log("selected ", artist);
    },
    slugify: function ($event) {
      var slug = this.$data.selected.name.toLowerCase().replace(/['.]/g, "");
      slug = slug.replace(/[^a-z0-9\s-]/g, " ")

      slug = slug.replace(/\s+/g, " ").trim().replace(/ /g, "-").trim('-');

      this.$data.selected.slug = slug;
    },
    addUpstreamSource: function () {
      this.$data.selected.upstream_sources.push({
        upstream_source_id: -1,
        upstream_identifier: ""
      });
    },
    removeUpstreamSource: function (idx) {
      this.$data.selected.upstream_sources.splice(idx, 1);
    },
    buildArtistPayload: function () {
      var r = {
        SlimArtist: {
          id: this.$data.selected.id,
          musicbrainz_id: this.$data.selected.musicbrainz_id,
          name: this.$data.selected.name,
          featured: parseInt(this.$data.selected.featured, 10),
          slug: this.$data.selected.slug,
          features: this.$data.selected.features
        },
        SlimUpstreamSources: this.$data.selected.upstream_sources
      };

      r.SlimArtist.features.id = 0;
      r.SlimArtist.features.artist_id = 0;

      return r;
    },
    sendArtistJson: async function (route, method, payload) {
      var response = await fetch("/api/v2/" + route, {
        method: method,
        headers: {
          'Accept': 'application/json',
          'Content-Type': 'application/json'
        },
        credentials: 'include',
        body: JSON.stringify(payload)
      });
      await readAdminResponse(response);
    },
    addArtist: function () {
      var payload = this.buildArtistPayload();

      payload.SlimArtist.id = 0;

      this.$data.loading = true;

      return this.sendArtistJson("artists", "POST", payload)
        .then(() => {
          this.$data.loading = false;
          this.$data.result = "Artist added";
        })
        .catch((error) => {
          this.$data.loading = false;
          this.$data.result = "Error: " + error.message;
        })
      ;
    },
    updateArtist: function () {
      this.$data.loading = true;
      var payload = this.buildArtistPayload();

      return this.sendArtistJson("artists/" + payload.SlimArtist.id, "PUT", payload)
        .then(() => {
          this.$data.loading = false;
          this.$data.result = "Artist updated";
        })
        .catch((error) => {
          this.$data.loading = false;
          this.$data.result = "Error: " + error.message;
        })
      ;
    }
  }
});

var refresh = new Vue({
  el: "#import",
  data: {
    artists: window.artists,
    loading: false,
    result: null,
    executionUrl: null,
    selected: {
      artist: -1,
      archive_deletion_limit: "",
      upstream_identifier: ""
    }
  },
  methods: {
    queueImport: function () {
      var identifier = this.selected.upstream_identifier.trim();
      var query = new URLSearchParams();
      var limit = this.selected.archive_deletion_limit;
      if (!identifier && limit !== "") {
        if (!Number.isSafeInteger(Number(limit)) || Number(limit) < 0 || Number(limit) > 2147483647) {
          this.result = "Error: Enter a whole deletion candidate limit of zero or greater.";
          this.executionUrl = null;
          return;
        }
        query.set("archiveDeletionLimit", limit);
      }

      var path = "/api/v2/import/" + encodeURIComponent(this.selected.artist);
      if (identifier) {
        path += "/" + encodeURIComponent(identifier);
      }
      return this.queueRequest(path + "?" + query.toString());
    },
    // Keep request state and the execution link consistent across all import controls.
    queueRequest: async function (url) {
      if (this.loading) {
        return;
      }
      this.loading = true;
      this.result = null;
      this.executionUrl = null;
      try {
        var response = await fetch(url, { method: "POST", credentials: "include" });
        this.result = await readAdminResponse(response);
        this.executionUrl = response.headers.get("Location");
      } catch (error) {
        this.result = "Error: " + error.message;
      } finally {
        this.loading = false;
      }
    },
    queueArchiveOrgIndex: function () {
      return this.queueRequest("/relisten-admin/index-archiveorg");
    },
    queueRefreshAllArtists: function () {
      return this.queueRequest("/relisten-admin/refresh-all-artists");
    },
    queueJerryGarciaVenueBackfill: function () {
      return this.queueRequest("/relisten-admin/backfill-jerrygarcia-venues?artistSlug=grateful-dead");
    }
  }
});
