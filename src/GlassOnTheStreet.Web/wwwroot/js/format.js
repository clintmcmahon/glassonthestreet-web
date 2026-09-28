// Every dynamically-rendered count on the site should route through this,
// so a number like 117576 always renders as "117,576" -- not sometimes
// formatted and sometimes not, depending on which script happened to
// remember to do it.
function formatNumber(value) {
  return Number(value).toLocaleString("en-US");
}
