import type { Page } from "@playwright/test";

export async function fakeGooglePicker(page: Page, file = "sheet") {
  await page.route("https://apis.google.com/js/api.js", route => route.fulfill({ contentType: "text/javascript", body: `
    window.gapi = {load(name, options) { if(name !== 'picker') throw Error('Unexpected SDK'); options.callback(); }};
    window.google = {picker: {
      ViewId:{DOCS:'docs'}, Action:{PICKED:'picked',CANCEL:'cancel'},
      View:class {setMimeTypes(types) {if(!types.includes('spreadsheet'))throw Error('Missing MIME filter');return this;}},
      PickerBuilder:class {
        setDeveloperKey() {return this;} setAppId() {return this;}
        setOAuthToken(value) {if(value!=='fixture-access')throw Error('Incorrect access token');return this;}
        setOrigin(value) {if(value!==location.origin)throw Error('Incorrect origin');return this;}
        setLocale() {return this;} addView() {return this;} setCallback(callback) {this.callback=callback;return this;}
        build() {const callback=this.callback;return {setVisible(value) {if(value)queueMicrotask(()=>callback({action:'picked',docs:[{id:${JSON.stringify(file)}}]}));},dispose() {}};}
      }
    }};` }));
}
