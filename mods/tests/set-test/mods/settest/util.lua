-- require("util") from the same folder, as SET documents.
local M = {}
function M.check(name, ok, detail)
  print((ok and "PASS " or "FAIL ") .. name .. (detail and (": " .. tostring(detail)) or ""))
end
return M
