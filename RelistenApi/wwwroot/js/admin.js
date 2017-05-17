
var addArtist = new Vue({
    el: "#add-artist",
    data: () => {
        var features = Object.keys(window.artists[0].features);
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
	        }
        }
    },
    methods: {
        artistForId: function(id) {
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
        slugify: function($event) {
            var slug = this.$data.selected.name.toLowerCase().replace(/['.]/g, "");
            slug = slug.replace(/[^a-z0-9\s-]/g, " ")

            slug = slug.replace(/\s+/g, " ").
                trim().
                replace(/ /g, "-").
                trim('-');

            this.$data.selected.slug = slug;
        },
        addUpstreamSource: function() {
            this.$data.selected.upstream_sources.push({
                upstream_source_id: -1,
                upstream_identifier: ""
            });
        },
        removeUpstreamSource: function (idx) {
            this.$data.selected.upstream_sources.splice(idx, 1);
        }
    }
})
