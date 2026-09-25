// Shared basemap retint, used by both the main map (map.js) and the
// report form's location picker (report-form.js). The hosted "liberty"
// style is a full-color OSM road atlas -- left as is, it clashes badly
// with a stark black/white/one-accent UI. Rather than a hand-rolled
// vector style, this retints the real layer ids of that style (checked
// directly against its style.json) toward a flat grayscale basemap, so
// the map reads as part of the same design system instead of a bolted-on
// Google Maps embed. Every call is wrapped so a renamed/missing layer id
// on a style update degrades silently rather than breaking the map.
function retintMapLibreBasemap(map) {
  const setMany = (ids, prop, value) => {
    for (const id of ids) {
      try {
        if (map.getLayer(id)) {
          map.setPaintProperty(id, prop, value);
        }
      } catch (e) {
        // non-fatal cosmetic tweak
      }
    }
  };

  setMany(["background"], "background-color", "#ffffff");

  // Land: flatten every land-use/land-cover category into one barely-
  // there neutral instead of the original green/tan/pink patchwork.
  setMany(
    [
      "park", "landuse_residential", "landcover_wood", "landcover_grass",
      "landcover_ice", "landuse_pitch", "landuse_track", "landuse_cemetery",
      "landuse_hospital", "landuse_school", "landcover_sand", "aeroway_fill"
    ],
    "fill-color", "#f5f5f6"
  );
  setMany(["park_outline"], "line-color", "#e4e4e7");

  setMany(["building"], "fill-color", "#f5f5f6");
  setMany(["building"], "fill-outline-color", "#e4e4e7");
  setMany(["building-3d"], "fill-extrusion-color", "#f0f0f1");

  // Water: one flat cool-gray fill, no more blue.
  setMany(["water"], "fill-color", "#e1e4e8");
  setMany(["waterway_river", "waterway_other", "waterway_tunnel"], "line-color", "#d7dade");

  // Roads: collapse the original class-by-color scheme (orange motorways,
  // yellow primaries, white minors, etc.) into a simple two-tone gray
  // hierarchy -- casings get a near-invisible halo tone, the road itself
  // a mid-gray whose darkness signals importance.
  const casings = [
    "tunnel_motorway_link_casing", "tunnel_service_track_casing", "tunnel_link_casing",
    "tunnel_street_casing", "tunnel_secondary_tertiary_casing", "tunnel_trunk_primary_casing",
    "tunnel_motorway_casing", "road_motorway_link_casing", "road_service_track_casing",
    "road_link_casing", "road_minor_casing", "road_secondary_tertiary_casing",
    "road_trunk_primary_casing", "road_motorway_casing", "bridge_motorway_link_casing",
    "bridge_service_track_casing", "bridge_link_casing", "bridge_street_casing",
    "bridge_path_pedestrian_casing", "bridge_secondary_tertiary_casing",
    "bridge_trunk_primary_casing", "bridge_motorway_casing"
  ];
  const minorRoads = [
    "tunnel_path_pedestrian", "tunnel_motorway_link", "tunnel_service_track", "tunnel_link",
    "tunnel_minor", "road_path_pedestrian", "road_motorway_link", "road_service_track",
    "road_link", "road_minor", "bridge_path_pedestrian", "bridge_motorway_link",
    "bridge_service_track", "bridge_link"
  ];
  const majorRoads = [
    "tunnel_secondary_tertiary", "tunnel_trunk_primary", "tunnel_motorway",
    "road_secondary_tertiary", "road_trunk_primary", "road_motorway",
    "bridge_secondary_tertiary", "bridge_trunk_primary", "bridge_motorway"
  ];
  const rail = [
    "tunnel_major_rail", "tunnel_major_rail_hatching", "tunnel_transit_rail", "tunnel_transit_rail_hatching",
    "road_major_rail", "road_major_rail_hatching", "road_transit_rail", "road_transit_rail_hatching",
    "bridge_major_rail", "bridge_major_rail_hatching", "bridge_transit_rail", "bridge_transit_rail_hatching"
  ];
  setMany(casings, "line-color", "#eef0f2");
  setMany(minorRoads, "line-color", "#d4d4d8");
  setMany(majorRoads, "line-color", "#b8b8bd");
  setMany(rail, "line-color", "#d4d4d8");
  setMany(["aeroway_runway", "aeroway_taxiway"], "line-color", "#e4e4e7");
  setMany(["boundary_2", "boundary_3", "boundary_disputed"], "line-color", "#c7c7cc");

  // Labels: one muted gray with a white halo, instead of the original
  // per-category label colors.
  setMany(
    [
      "waterway_line_label", "water_name_point_label", "water_name_line_label",
      "poi_r20", "poi_r7", "poi_r1", "poi_transit", "highway-name-path",
      "highway-name-minor", "highway-name-major", "airport", "label_other",
      "label_village", "label_town", "label_state", "label_city",
      "label_city_capital", "label_country_3", "label_country_2", "label_country_1"
    ],
    "text-color", "#6b7280"
  );
  setMany(
    [
      "waterway_line_label", "water_name_point_label", "water_name_line_label",
      "poi_r20", "poi_r7", "poi_r1", "poi_transit", "airport", "label_other",
      "label_village", "label_town", "label_state", "label_city",
      "label_city_capital", "label_country_3", "label_country_2", "label_country_1"
    ],
    "text-halo-color", "#ffffff"
  );
}
