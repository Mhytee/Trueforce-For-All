// Google Apps Script: creates the "Trueforce engine data" Google Form,
// links a response spreadsheet, and prints the prefilled URL the plugin
// needs to embed.
//
// One-time setup:
//   1. Go to https://script.google.com/ and click "New project".
//   2. Replace the default Code.gs contents with this whole file.
//   3. Click Save (disk icon), then Run -> select createTrueforceForm.
//      First run will prompt you to authorize Drive + Forms access.
//   4. After it finishes, open View -> Logs (or Execution log).
//      Copy the line starting with "PLUGIN URL TEMPLATE:".
//   5. Send me that line OR drop it into SettingsControl.xaml.cs in
//      place of EngineDataFormUrlTemplate (see the constant near the
//      top of the click handler).
//
// The form has a single Paragraph (long-answer) field. The plugin
// pre-fills it with the same structured markdown body it currently
// builds for the GitHub flow, so submissions land in the linked sheet
// as one cell per row -- easy to scan, sort, and batch-process into
// bake updates.

function createTrueforceForm() {
  // 1. Create the form.
  const form = FormApp.create('Trueforce engine data')
    .setTitle('Trueforce engine data')
    .setDescription(
      'Submit engine data corrections or contributions for cars in the ' +
      'Trueforce For All SimHub plugin. Pre-filled by the plugin -- ' +
      'just hit Submit.')
    .setCollectEmail(false)
    .setLimitOneResponsePerUser(false)
    .setAllowResponseEdits(false)
    .setShowLinkToRespondAgain(false);

  // 2. Add the single paragraph field.
  const item = form.addParagraphTextItem()
    .setTitle('Engine data submission')
    .setHelpText('Pre-filled by the plugin. You can edit before submitting.')
    .setRequired(true);

  // 3. Link a fresh response spreadsheet so submissions land somewhere
  //    queryable. Putting it next to the form in Drive keeps things tidy.
  const sheet = SpreadsheetApp.create('Trueforce engine data responses');
  form.setDestination(FormApp.DestinationType.SPREADSHEET, sheet.getId());

  // 4. Build the prefilled-URL template the plugin will use. The form's
  //    public URL has shape:
  //      https://docs.google.com/forms/d/e/<FORM_ID>/viewform
  //    and prefill query is:
  //      ?usp=pp_url&entry.<ENTRY_ID>=<URL_ENCODED_VALUE>
  //    where <ENTRY_ID> is the integer returned by item.getId().
  const entryId = item.getId();
  const publicUrl = form.getPublishedUrl();
  const prefilledTemplate = publicUrl + '?usp=pp_url&entry.' + entryId + '={BODY}';

  // 5. Log everything useful.
  Logger.log('Form edit URL:        ' + form.getEditUrl());
  Logger.log('Form public URL:      ' + publicUrl);
  Logger.log('Field entry ID:       ' + entryId);
  Logger.log('Response sheet URL:   ' + sheet.getUrl());
  Logger.log('');
  Logger.log('PLUGIN URL TEMPLATE: ' + prefilledTemplate);
  Logger.log('  Replace {BODY} with the URL-encoded markdown payload.');
}
