require File.dirname(__FILE__) + '/../../spec_helper'
require File.dirname(__FILE__) + '/fixtures/classes'
require File.dirname(__FILE__) + '/shared/exit'

describe "Thread#kill" do
  it_behaves_like :thread_exit, :kill
end
