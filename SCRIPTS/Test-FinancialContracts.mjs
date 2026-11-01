import assert from 'node:assert/strict';
import fs from 'node:fs';
const spec=JSON.parse(fs.readFileSync(new URL('../DOCS/CONTRACTS/transactions-v1.openapi.json',import.meta.url),'utf8'));
assert.equal(spec.openapi,'3.0.3');
assert.match(spec.info.description,/DEVELOPMENT ONLY/);
function visit(node){
 if(!node||typeof node!=='object')return;
 if(node.$ref){
  assert(node.$ref.startsWith('#/'));
  let target=spec;
  for(const key of node.$ref.slice(2).split('/'))target=target?.[key.replaceAll('~1','/').replaceAll('~0','~')];
  assert(target,'Unresolved contract reference');
 }
 for(const child of Object.values(node))visit(child);
}
visit(spec);
const ids=new Set();
for(const [path,item] of Object.entries(spec.paths)){
 assert(path.startsWith('/api/v1/txn/'));
 for(const [verb,operation] of Object.entries(item)){
  assert(!ids.has(operation.operationId));ids.add(operation.operationId);
  for(const token of path.matchAll(/\{([^}]+)\}/g))assert(operation.parameters.some(p=>p.in==='path'&&p.name===token[1]&&p.required));
  if(verb==='post'){
   for(const header of ['Idempotency-Key','X-CSRF-TOKEN','Origin'])assert(operation.parameters.some(p=>p.in==='header'&&p.name===header&&p.required));
   assert(operation.responses['200']&&operation.responses['201']&&operation.responses['409']&&operation.responses['503']);
  }
 }
}
for(const name of ['CreateTransferIntentRequest','ConfirmTransferIntentRequest']){
 const schema=spec.components.schemas[name];assert.equal(schema.additionalProperties,false);
 for(const forbidden of ['programUuid','customerUuid','userUuid','coreSessionUuid','authSessionUuid','balance','permission'])
  assert(!Object.hasOwn(schema.properties,forbidden));
}
const amount=spec.components.schemas.Amount.properties.minorUnits;
assert.equal(amount.type,'string');
assert(new RegExp(amount.pattern).test('9007199254740993'));
for(const invalid of ['0','01','-1','1.0','1e2',' 1'])assert(!new RegExp(amount.pattern).test(invalid));
assert.equal(spec.components.schemas.ConfirmTransferIntentRequest.properties.reviewVersion.enum[0],1);
console.log('PASS: financial OpenAPI references, paths, strict request bodies, amount strings and mutation security/idempotency requirements.');
console.log('These are contract sanity checks; runtime behavior is checked separately by Test-Finance.ps1 -Api.');
