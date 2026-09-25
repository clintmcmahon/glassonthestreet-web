// Shared basemap retint, used by both the main map (map.js) and the
// report form's location picker (report-form.js). The hosted "liberty"
// style's stock colors (bright orange motorways, saturated green parks,
// deep blue water) read too loud against the site's UI. This retints the
// real layer ids of that style (checked directly against its style.json)
// toward a muted, tasteful palette in the same family as a modern light
// map (soft greens for parks, soft blue for water, warm neutrals for
// roads/buildings) rather than flattening everything to gray. Every call
// is wrapped so a renamed/missing layer id on a style update degrades
// silently rather than breaking the map.
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

  setMany(["background"], "background-color", "#faf9f6");

  // Land: soft, distinguishable categories instead of one flat gray.
  setMany(["park", "landcover_wood", "landcover_grass", "landuse_cemetery"], "fill-color", "#dbe7d9");
  setMany(["park_outline"], "line-color", "#c7d6c4");
  setMany(["landcover_ice"], "fill-color", "#eef2f4");
  setMany(["landuse_residential"], "fill-color", "#f2efe8");
  setMany(["landuse_pitch", "landuse_track"], "fill-color", "#e3e6d4");
  setMany(["landuse_hospital"], "fill-color", "#eceef4");
  setMany(["landuse_school"], "fill-color", "#f1e9d6");
  setMany(["landcover_sand"], "fill-color", "#f1e7d1");
  setMany(["aeroway_fill"], "fill-color", "#ececea");

  setMany(["building"], "fill-color", "#eee7da");
  setMany(["building"], "fill-outline-color", "#ddd2bd");
  setMany(["building-3d"], "fill-extrusion-color", "#eee7da");

  // Water: a real, if muted, blue -- not gray.
  setMany(["water"], "fill-color", "#bcd6e0");
  setMany(["waterway_river", "waterway_other", "waterway_tunnel"], "line-color", "#a9c7d3");

  // Roads: keep the familiar warm hierarchy (motorways read warmer/more
  // saturated than minor streets) but pull every tone down to a muted,
  // pastel register instead of the stock style's saturated colors.
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
  const midRoads = [
    "tunnel_secondary_tertiary", "road_secondary_tertiary", "bridge_secondary_tertiary"
  ];
  const majorRoads = [
    "tunnel_trunk_primary", "tunnel_motorway", "road_trunk_primary", "road_motorway",
    "bridge_trunk_primary", "bridge_motorway"
  ];
  const rail = [
    "tunnel_major_rail", "tunnel_major_rail_hatching", "tunnel_transit_rail", "tunnel_transit_rail_hatching",
    "road_major_rail", "road_major_rail_hatching", "road_transit_rail", "road_transit_rail_hatching",
    "bridge_major_rail", "bridge_major_rail_hatching", "bridge_transit_rail", "bridge_transit_rail_hatching"
  ];
  setMany(casings, "line-color", "#f0ebe0");
  setMany(minorRoads, "line-color", "#fdfcf9");
  setMany(midRoads, "line-color", "#f3d9ae");
  setMany(majorRoads, "line-color", "#eebd7e");
  setMany(rail, "line-color", "#c9c2b3");
  setMany(["aeroway_runway", "aeroway_taxiway"], "line-color", "#e4e0d6");
  setMany(["boundary_2", "boundary_3", "boundary_disputed"], "line-color", "#c9beac");

  // Labels: one warm muted gray with a paper-colored halo.
  setMany(
    [
      "waterway_line_label", "water_name_point_label", "water_name_line_label",
      "poi_r20", "poi_r7", "poi_r1", "poi_transit", "highway-name-path",
      "highway-name-minor", "highway-name-major", "airport", "label_other",
      "label_village", "label_town", "label_state", "label_city",
      "label_city_capital", "label_country_3", "label_country_2", "label_country_1"
    ],
    "text-color", "#57534e"
  );
  setMany(
    [
      "waterway_line_label", "water_name_point_label", "water_name_line_label",
      "poi_r20", "poi_r7", "poi_r1", "poi_transit", "airport", "label_other",
      "label_village", "label_town", "label_state", "label_city",
      "label_city_capital", "label_country_3", "label_country_2", "label_country_1"
    ],
    "text-halo-color", "#faf9f6"
  );
}
