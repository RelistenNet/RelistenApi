import assert from "node:assert/strict";
import { test } from "node:test";
import { ArtistFeature, hasArtistFeature } from "./artist-features.ts";

test("checks named flags on the decimal string returned by the API", () => {
  const artist = Object.freeze({ features: "10485761" });

  assert.equal(hasArtistFeature(artist, ArtistFeature.Descriptions), true);
  assert.equal(hasArtistFeature(artist, ArtistFeature.TrackDurations), true);
  assert.equal(hasArtistFeature(artist, ArtistFeature.Normalization), true);
  assert.equal(hasArtistFeature(artist, ArtistFeature.Ratings), false);
  assert.equal(hasArtistFeature({ features: "0" }, ArtistFeature.Normalization), false);
  assert.equal(artist.features, "10485761");
});

test("preserves low flags when unknown high bits exceed JavaScript's integer range", () => {
  const artist = { features: "9223372036854775809" }; // Bit 63 plus descriptions.

  assert.equal(hasArtistFeature(artist, ArtistFeature.Descriptions), true);
  assert.equal(hasArtistFeature(artist, ArtistFeature.Normalization), false);
  assert.equal(
    hasArtistFeature({ features: "9223372036854775808" }, ArtistFeature.Descriptions),
    false,
  );
  for (const feature of Object.values(ArtistFeature)) {
    assert.equal(hasArtistFeature({ features: "18446744073709551615" }, feature), true);
  }
});
