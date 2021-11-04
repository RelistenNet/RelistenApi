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
    sendArtistJson: function (route, method, payload) {
      return fetch("/api/v2/" + route, {
        method: method,
        headers: {
          'Accept': 'application/json',
          'Content-Type': 'application/json'
        },
        credentials: 'include',
        body: JSON.stringify(payload)
      });
    },
    addArtist: function () {
      var payload = this.buildArtistPayload();

      payload.SlimArtist.id = 0;

      this.$data.loading = true;

      this.sendArtistJson("artists", "POST", payload)
        .then(() => {
          this.$data.loading = false;
          this.$data.result = "Artist added";
        })
        .catch((res) => {
          this.$data.loading = false;

          res.text().then((text) => {
            this.$data.result = "Error: " + text;
          });
        })
      ;
    },
    updateArtist: function () {
      var payload = this.buildArtistPayload();

      this.sendArtistJson("artists/" + payload.SlimArtist.id, "PUT", payload)
        .then(() => {
          this.$data.loading = false;
          this.$data.result = "Artist updated";
        })
        .catch((res) => {
          this.$data.loading = false;

          res.text().then((text) => {
            this.$data.result = "Error: " + text;
          });
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

    selected: {
      artist: -1,
      delete_existing: false,
      upstream_identifier: ""
    }
  },
  methods: {
    queueImport: function () {
      var specificShow = this.$data.selected.upstream_identifier.length > 0 ? "/" + this.$data.selected.upstream_identifier : "";

      fetch("/api/v2/import/" + this.$data.selected.artist + specificShow + "?deleteOldContent=" + this.$data.selected.delete_existing, {
        credentials: 'include'
      })
        .then((res) => {
          this.$data.loading = false;

          res.text().then((text) => {
            this.$data.result = "Artist update queued: " + text;
          });
        })
        .catch((res) => {
          this.$data.loading = false;

          res.text().then((text) => {
            this.$data.result = "Error: " + text;
          });
        })
      ;
    }
  }
})
