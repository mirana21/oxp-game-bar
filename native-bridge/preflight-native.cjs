'use strict';
// Read-only startup eligibility check, deliberately separate from the launcher.
try {
  require('./launch-native.cjs').verifyVendor();
  process.exitCode = 0;
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
